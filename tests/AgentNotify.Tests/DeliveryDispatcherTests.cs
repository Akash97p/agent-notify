using System.Security.Cryptography;
using AgentNotify.Contracts;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Logging;

namespace AgentNotify.Tests;

public sealed class DeliveryDispatcherTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(),
        $"an-dispatch-{Guid.NewGuid():N}.db");
    private SqliteDeliveryRepository _repository = null!;
    private ProviderProfileService _profiles = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteDeliveryRepository(_db);
        await _repository.InitializeAsync();
        _profiles = new ProviderProfileService(
            _repository,
            new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
    }

    public Task DisposeAsync()
    {
        SqliteConnectionPoolClear();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_db + suffix); } catch { }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Coordinator_QueuesMatchingEnabledRouteOnlyOnce()
    {
        var (profile, route) = await CreateProviderAndRouteAsync();
        var notification = MakeNotification();
        var signaled = 0;
        var coordinator = new NotificationDeliveryCoordinator(
            _repository,
            () => signaled++);

        Assert.Equal(1, await coordinator.EnqueueAsync(notification));
        Assert.Equal(0, await coordinator.EnqueueAsync(notification));

        var queued = Assert.Single(await _repository.ListOutboxAsync());
        Assert.Equal(profile.Id, queued.ProviderId);
        Assert.Equal(route.Id, queued.RouteId);
        Assert.Equal(1, signaled);
    }

    [Fact]
    public async Task Dispatcher_DeliversAndRecordsSanitizedAttempt()
    {
        await CreateProviderAndRouteAsync();
        var adapter = new RecordingAdapter("webhook", DeliveryResult.Success(204));
        var dispatcher = CreateDispatcher(adapter);
        await new NotificationDeliveryCoordinator(_repository).EnqueueAsync(MakeNotification());

        Assert.True(await dispatcher.ProcessOneAsync());

        var item = Assert.Single(await _repository.ListOutboxAsync());
        Assert.Equal(OutboxStatus.Delivered, item.Status);
        var attempt = Assert.Single(await _repository.ListAttemptsAsync(item.Id));
        Assert.True(attempt.Succeeded);
        Assert.Equal(204, attempt.StatusCode);
        Assert.Equal("top-secret", Assert.Single(adapter.Deliveries).Secrets["token"]);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task Dispatcher_RetriesTransientFailureAndDeadLettersAtLimit()
    {
        var (profile, route) = await CreateProviderAndRouteAsync();
        var item = new OutboxItem
        {
            NotificationId = "retry-limit",
            RouteId = route.Id,
            ProviderId = profile.Id,
            AttemptCount = RetrySchedule.MaximumAttempts - 1,
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        await _repository.EnqueueAsync(item);
        var adapter = new RecordingAdapter(
            "webhook",
            DeliveryResult.Retry("HTTP response included secret https://token.example", 503));
        var dispatcher = CreateDispatcher(adapter);

        Assert.True(await dispatcher.ProcessOneAsync());

        var stored = Assert.Single(await _repository.ListOutboxAsync());
        Assert.Equal(OutboxStatus.DeadLetter, stored.Status);
        Assert.Equal(RetrySchedule.MaximumAttempts, stored.AttemptCount);
        var attempt = Assert.Single(await _repository.ListAttemptsAsync(item.Id));
        Assert.Equal("adapter_error", attempt.ErrorCode);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task Dispatcher_TimeoutBecomesBoundedRetry()
    {
        await CreateProviderAndRouteAsync();
        await new NotificationDeliveryCoordinator(_repository).EnqueueAsync(MakeNotification());
        var adapter = new RecordingAdapter("webhook", null, waitForCancellation: true);
        var dispatcher = CreateDispatcher(adapter, TimeSpan.FromMilliseconds(30));

        Assert.True(await dispatcher.ProcessOneAsync());

        var item = Assert.Single(await _repository.ListOutboxAsync());
        Assert.Equal(OutboxStatus.Retry, item.Status);
        Assert.Equal("timeout", Assert.Single(await _repository.ListAttemptsAsync(item.Id)).ErrorCode);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task InterruptedProcessing_IsRecoveredForNextRun()
    {
        var (profile, route) = await CreateProviderAndRouteAsync();
        await _repository.EnqueueAsync(new OutboxItem
        {
            NotificationId = "interrupted",
            RouteId = route.Id,
            ProviderId = profile.Id,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.NotNull(await _repository.ClaimDueAsync(DateTimeOffset.UtcNow));

        var recovered = await _repository.RecoverInterruptedAsync(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Equal(1, recovered);
        Assert.NotNull(await _repository.ClaimDueAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Diagnostics_CountStatesWithoutPayloadOrSecrets()
    {
        await CreateProviderAndRouteAsync();
        await new NotificationDeliveryCoordinator(_repository).EnqueueAsync(MakeNotification());
        var dispatcher = CreateDispatcher(new RecordingAdapter("webhook", DeliveryResult.Success()));

        var diagnostics = await dispatcher.GetDiagnosticsAsync();

        Assert.Equal(1, diagnostics.Pending);
        Assert.Equal(["webhook"], diagnostics.RegisteredAdapters);
        Assert.DoesNotContain("top-secret", System.Text.Json.JsonSerializer.Serialize(diagnostics));
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task TestProvider_UsesBoundedAdapterPathWithoutPersistingPayload()
    {
        var (profile, _) = await CreateProviderAndRouteAsync();
        var adapter = new RecordingAdapter("webhook", DeliveryResult.Success(202));
        var dispatcher = CreateDispatcher(adapter);

        var result = await dispatcher.TestProviderAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(202, result.StatusCode);
        Assert.StartsWith("test-", Assert.Single(adapter.Deliveries).OutboxId);
        Assert.Empty(await _repository.ListOutboxAsync());
        await dispatcher.DisposeAsync();
    }

    private DeliveryDispatcher CreateDispatcher(
        IOutboundChannelAdapter adapter,
        TimeSpan? timeout = null) =>
        new(
            _repository,
            _profiles,
            [adapter],
            new RecordingLogger(),
            timeout,
            () => 0);

    private async Task<(ProviderProfile Profile, DeliveryRoute Route)> CreateProviderAndRouteAsync()
    {
        var profile = await _profiles.SaveAsync(
            null,
            "Test webhook",
            "webhook",
            true,
            "{}",
            new Dictionary<string, string> { { "token", "top-secret" } });
        var route = new DeliveryRoute
        {
            Name = "Important errors",
            ProviderId = profile.Id,
            Enabled = true,
            TypeId = NotificationTypes.Error,
            MinimumPriority = NotificationPriority.High
        };
        await _repository.UpsertRouteAsync(route);
        return (profile, route);
    }

    private static Notification MakeNotification() =>
        Notification.Create(new CreateNotificationRequest
        {
            Title = "Build failed",
            Message = "Compiler error",
            Type = NotificationTypes.Error,
            Priority = NotificationPriority.Critical,
            Agent = "codex",
            Project = "agent-notify"
        }, DateTimeOffset.UtcNow);

    private static void SqliteConnectionPoolClear() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class RecordingAdapter : IOutboundChannelAdapter
    {
        private readonly DeliveryResult? _result;
        private readonly bool _waitForCancellation;

        public RecordingAdapter(
            string kind,
            DeliveryResult? result,
            bool waitForCancellation = false)
        {
            Kind = kind;
            _result = result;
            _waitForCancellation = waitForCancellation;
        }

        public string Kind { get; }
        public List<OutboundDelivery> Deliveries { get; } = [];

        public async Task<DeliveryResult> DeliverAsync(
            OutboundDelivery delivery,
            CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            if (_waitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return _result!;
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
