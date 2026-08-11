using System.Security.Cryptography;
using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

public sealed class DeliveryFoundationTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(),
        $"an-delivery-{Guid.NewGuid():N}.db");
    private SqliteDeliveryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteDeliveryRepository(_db);
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_db + suffix);
            }
            catch
            {
                // Best-effort cleanup must not hide the test result.
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProviderSecrets_AreEncryptedAndRedacted()
    {
        var service = CreateProfileService();
        var profile = await service.SaveAsync(
            null,
            "Telegram alerts",
            "telegram",
            true,
            "{\"chatId\":\"42\"}",
            new Dictionary<string, string> { { "bot_token", "plain-secret-value" } });

        Assert.Equal(["bot_token"], profile.SecretNames);
        Assert.DoesNotContain(
            "plain-secret-value",
            JsonSerializer.Serialize(profile),
            StringComparison.Ordinal);
        Assert.Equal(
            "plain-secret-value",
            (await service.GetSecretsForDeliveryAsync(profile.Id))["bot_token"]);

        var stored = await _repository.GetProviderAsync(profile.Id);
        Assert.StartsWith("aes-gcm:v1:", stored!.EncryptedSecrets);
        Assert.DoesNotContain(
            "plain-secret-value",
            stored.EncryptedSecrets,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderUpdate_WithNullSecrets_PreservesEncryptedCredentials()
    {
        var service = CreateProfileService();
        var profile = await service.SaveAsync(
            null,
            "Hook",
            "webhook",
            true,
            "{}",
            new Dictionary<string, string> { { "token", "keep-me" } });
        var originalEnvelope = (await _repository.GetProviderAsync(profile.Id))!.EncryptedSecrets;

        await service.SaveAsync(profile.Id, "Renamed hook", "webhook", false, "{}", null);

        Assert.Equal("keep-me", (await service.GetSecretsForDeliveryAsync(profile.Id))["token"]);
        Assert.Equal(originalEnvelope, (await _repository.GetProviderAsync(profile.Id))!.EncryptedSecrets);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public async Task ProviderConfig_RejectsNonObjectJson(string configJson)
    {
        var service = CreateProfileService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(null, "Hook", "webhook", true, configJson, null));
    }

    [Fact]
    public async Task Routes_OutboxClaimAndAttempts_AreDurable()
    {
        var service = CreateProfileService();
        var profile = await service.SaveAsync(
            null,
            "Hook",
            "webhook",
            true,
            "{}",
            new Dictionary<string, string>());
        var route = new DeliveryRoute
        {
            Name = "Errors",
            ProviderId = profile.Id,
            Enabled = true,
            MinimumPriority = NotificationPriority.High,
            TypeId = "error"
        };
        await _repository.UpsertRouteAsync(route);
        Assert.Single(await _repository.ListRoutesAsync());

        var item = new OutboxItem
        {
            NotificationId = "n1",
            RouteId = route.Id,
            ProviderId = profile.Id,
            PayloadJson = "{}"
        };
        await _repository.EnqueueAsync(item);

        var claimed = await _repository.ClaimDueAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotNull(claimed);
        Assert.Equal(OutboxStatus.Processing, claimed!.Status);

        claimed.Status = OutboxStatus.Delivered;
        claimed.AttemptCount = 1;
        claimed.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.CompleteAttemptAsync(claimed, new DeliveryAttempt
        {
            OutboxId = claimed.Id,
            AttemptNumber = 1,
            Succeeded = true
        });

        Assert.True(Assert.Single(await _repository.ListAttemptsAsync(claimed.Id)).Succeeded);
        Assert.Null(await _repository.ClaimDueAsync(DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task ClaimDue_IsAtomicAcrossConcurrentWorkers()
    {
        var (route, profile) = await CreateRouteAsync();
        await _repository.EnqueueAsync(new OutboxItem
        {
            NotificationId = "single-delivery",
            RouteId = route.Id,
            ProviderId = profile.Id
        });
        var secondRepository = new SqliteDeliveryRepository(_db);
        await secondRepository.InitializeAsync();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        var claims = await Task.WhenAll(
            _repository.ClaimDueAsync(now),
            secondRepository.ClaimDueAsync(now));

        Assert.Single(claims, claim => claim is not null);
    }

    [Fact]
    public async Task SchemaMigration_IsRecorded()
    {
        await using var connection = new SqliteConnection($"Data Source={_db}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NewerSchemaVersion_IsRejected()
    {
        await using (var connection = new SqliteConnection($"Data Source={_db}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO schema_migrations(version, applied_at) VALUES (999, 'future');";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteDeliveryRepository(_db).InitializeAsync());
        Assert.Contains("version 999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingAndRetryPolicy_AreDeterministic()
    {
        var notification = Notification.Create(new CreateNotificationRequest
        {
            Title = "Failed",
            Message = "details",
            Type = "error",
            Priority = NotificationPriority.Critical,
            Agent = "codex",
            Project = "agent-notify"
        }, DateTimeOffset.UtcNow);
        var route = new DeliveryRoute
        {
            Enabled = true,
            MinimumPriority = NotificationPriority.High,
            TypeId = "error",
            Agent = "CODEX",
            Project = "agent-notify",
            IncludeMessage = false
        };

        Assert.True(DeliveryRouting.Matches(route, notification));
        Assert.DoesNotContain("details", DeliveryRouting.CreatePayload(notification, route.IncludeMessage));
        Assert.True(RetrySchedule.DelayFor(2) > RetrySchedule.DelayFor(1));
        Assert.True(RetrySchedule.DelayFor(99) <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Dpapi_RoundTripsForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var protector = new DpapiSecretProtector();
        var envelope = protector.Protect("dpapi-test-secret");

        Assert.StartsWith("dpapi-user:v1:", envelope);
        Assert.DoesNotContain("dpapi-test-secret", envelope);
        Assert.Equal("dpapi-test-secret", protector.Unprotect(envelope));
    }

    private ProviderProfileService CreateProfileService() =>
        new(
            _repository,
            new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));

    private async Task<(DeliveryRoute Route, ProviderProfile Profile)> CreateRouteAsync()
    {
        var profile = await CreateProfileService().SaveAsync(
            null,
            "Concurrent hook",
            "webhook",
            true,
            "{}",
            new Dictionary<string, string>());
        var route = new DeliveryRoute
        {
            Name = "Concurrent",
            ProviderId = profile.Id,
            Enabled = true
        };
        await _repository.UpsertRouteAsync(route);
        return (route, profile);
    }
}
