using AgentNotify.Protocol;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;

namespace AgentNotify.Tests;

public sealed class InteractionServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteInteractionRepository _repo = null!;
    private InteractionService _service = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"an-interactions-{Guid.NewGuid():N}.db");
        _repo = new SqliteInteractionRepository(_dbPath);
        await _repo.InitializeAsync();
        _service = new InteractionService(_repo);
    }

    public async Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        await Task.CompletedTask;
    }

    private static CreateInteractionRequest Permission(string prompt = "Deploy to prod?", string? key = null) => new()
    {
        Key = key,
        Agent = "codex",
        Project = "shop",
        SessionId = "sess-1",
        Kind = InteractionKind.Permission,
        Prompt = prompt,
        Choices =
        [
            new InteractionChoice { Id = "allow-once", Label = "Allow once" },
            new InteractionChoice { Id = "deny", Label = "Deny" }
        ],
        TtlSeconds = 300
    };

    [Fact]
    public async Task Request_CreatesPendingWithDigestAndNonce()
    {
        var result = await _service.RequestAsync(Permission());
        Assert.Null(result.Error);
        Assert.True(result.WasCreated);
        var item = result.Value!;
        Assert.Equal(InteractionStatus.Pending, item.Status);
        Assert.Equal(64, item.RequestDigest.Length);
        Assert.NotEmpty(item.Nonce);
        Assert.True(item.ExpiresAt > item.CreatedAt);
    }

    [Fact]
    public async Task Request_RejectsBadShapes()
    {
        Assert.NotNull((await _service.RequestAsync(new CreateInteractionRequest
            { Kind = InteractionKind.Permission, Prompt = "x", Choices = [new InteractionChoice { Id = "only", Label = "Only" }] })).Error);
        Assert.NotNull((await _service.RequestAsync(new CreateInteractionRequest
            { Kind = InteractionKind.Text, Prompt = "x", Choices = [new InteractionChoice { Id = "a", Label = "A" }, new InteractionChoice { Id = "b", Label = "B" }] })).Error);
        Assert.NotNull((await _service.RequestAsync(new CreateInteractionRequest
            { Kind = InteractionKind.Text, Prompt = "   " })).Error);
        var dup = Permission();
        dup.Choices![1].Id = "allow-once";
        Assert.NotNull((await _service.RequestAsync(dup)).Error);
    }

    [Fact]
    public async Task Request_SameKeyReusesPending()
    {
        var first = await _service.RequestAsync(Permission(key: "k1"));
        var second = await _service.RequestAsync(Permission(key: "k1"));
        Assert.Null(second.Error);
        Assert.False(second.WasCreated);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
    }

    [Fact]
    public async Task Request_ChangedPromptSupersedesKeyedPending()
    {
        var first = await _service.RequestAsync(Permission(key: "k2"));
        var second = await _service.RequestAsync(Permission("Wipe the database?", "k2"));
        Assert.Null(second.Error);
        Assert.True(second.WasCreated);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        var old = await _service.GetAsync(first.Value.Id);
        Assert.Equal(InteractionStatus.Superseded, old.Value!.Status);
    }

    [Fact]
    public async Task Respond_AcceptsValidChoice()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var result = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r1",
            RequestDigest = item.RequestDigest,
            Nonce = item.Nonce,
            ChoiceId = "deny",
            Source = "desktop"
        });
        Assert.Null(result.Error);
        Assert.Equal(InteractionStatus.Answered, result.Value!.Status);
        Assert.Equal("deny", result.Value.Response!.ChoiceId);
        Assert.Equal("desktop", result.Value.Response.Source);
    }

    [Fact]
    public async Task Respond_FirstValidWins()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var first = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "deny", Source = "desktop"
        });
        Assert.Null(first.Error);
        var second = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r2", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "allow-once", Source = "relay"
        });
        Assert.NotNull(second.Error);
        Assert.Contains("already answered", second.Error);
    }

    [Fact]
    public async Task Respond_SameResponseIdReplaysOriginal()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var req = new RespondInteractionRequest
        {
            ResponseId = "r1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "deny", Source = "relay"
        };
        var first = await _service.RespondAsync(item.Id, req);
        var replay = await _service.RespondAsync(item.Id, req);
        Assert.Null(replay.Error);
        Assert.Equal(first.Value!.Response!.ResponseId, replay.Value!.Response!.ResponseId);
    }

    [Fact]
    public async Task Respond_RejectsDigestMismatchAndUnknownChoice()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var stale = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r9", RequestDigest = new string('0', 64), Nonce = item.Nonce,
            ChoiceId = "deny", Source = "cli"
        });
        Assert.NotNull(stale.Error);
        Assert.Contains("digest", stale.Error);

        var unknown = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r10", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "allow-always", Source = "cli"
        });
        Assert.NotNull(unknown.Error);
        Assert.Contains("offered", unknown.Error);

        var wrongNonce = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r11", RequestDigest = item.RequestDigest, Nonce = "bogus", ChoiceId = "deny", Source = "cli"
        });
        Assert.NotNull(wrongNonce.Error);
        Assert.Contains("nonce", wrongNonce.Error);

        var missingNonce = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "r12", RequestDigest = item.RequestDigest, ChoiceId = "deny", Source = "cli"
        });
        Assert.NotNull(missingNonce.Error);
        Assert.Contains("nonce", missingNonce.Error);
    }

    [Fact]
    public async Task Respond_TextKindBounds()
    {
        var created = await _service.RequestAsync(new CreateInteractionRequest
        {
            Agent = "claude", Kind = InteractionKind.Text, Prompt = "Name it?", TextMaxLength = 10
        });
        var item = created.Value!;
        var tooLong = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "t1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            Text = "way too long an answer", Source = "cli"
        });
        Assert.NotNull(tooLong.Error);
        var ok = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "t2", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            Text = "short", Source = "cli"
        });
        Assert.Null(ok.Error);
        Assert.Equal("short", ok.Value!.Response!.Text);
    }

    [Fact]
    public async Task ExpiredInteractionsCannotBeAnswered()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        item.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _repo.UpdateAsync(item);

        var result = await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "e1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "deny", Source = "cli"
        });
        Assert.NotNull(result.Error);
        Assert.Contains("expired", result.Error);
    }

    [Fact]
    public async Task Cancel_SettlesPendingAndIsIdempotent()
    {
        var created = await _service.RequestAsync(Permission());
        var cancelled = await _service.CancelAsync(created.Value!.Id);
        Assert.Equal(InteractionStatus.Cancelled, cancelled.Value!.Status);
        var again = await _service.CancelAsync(created.Value.Id);
        Assert.Equal(InteractionStatus.Cancelled, again.Value!.Status);

        var answer = await _service.RespondAsync(created.Value.Id, new RespondInteractionRequest
        {
            ResponseId = "c1", RequestDigest = created.Value.RequestDigest, Nonce = created.Value.Nonce,
            ChoiceId = "deny", Source = "cli"
        });
        Assert.NotNull(answer.Error);
    }

    [Fact]
    public async Task Wait_ReturnsWhenAnswered()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var waiter = _service.WaitAsync(item.Id, TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "w1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "allow-once", Source = "desktop"
        });
        var settled = await waiter.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(settled);
        Assert.Equal(InteractionStatus.Answered, settled!.Status);
    }

    [Fact]
    public async Task Wait_TimesOutWithPendingState()
    {
        var created = await _service.RequestAsync(Permission());
        var settled = await _service.WaitAsync(created.Value!.Id, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(settled);
        Assert.Equal(InteractionStatus.Pending, settled!.Status);
    }

    /// <summary>
    /// Nothing signals a waiter when the deadline merely passes, so the timeout
    /// path has to settle the row itself. Reporting "pending" for a question that
    /// is already dead sends the ask hook back for another whole wait slice.
    /// </summary>
    [Fact]
    public async Task Wait_TimingOutPastTheDeadlineReportsExpired()
    {
        var created = await _service.RequestAsync(Permission());
        var item = await _repo.GetByIdAsync(created.Value!.Id);
        // Straight to the repository: the service clamps a TTL to 30 s minimum,
        // and the point here is a deadline that lapses mid-wait.
        item!.ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(250);
        await _repo.UpdateAsync(item);

        var settled = await _service.WaitAsync(item.Id, TimeSpan.FromSeconds(2));

        Assert.NotNull(settled);
        Assert.Equal(InteractionStatus.Expired, settled!.Status);
        var stored = await _repo.GetByIdAsync(item.Id);
        Assert.Equal(InteractionStatus.Expired, stored!.Status);
    }

    /// <summary>
    /// A broker runs for weeks. One retained empty waiter list per question it was
    /// ever asked is a leak, so the bucket goes when its last waiter does.
    /// </summary>
    [Fact]
    public async Task Wait_ReleasesItsWaiterBucket()
    {
        var timedOut = await _service.RequestAsync(Permission("one?", "wb1"));
        await _service.WaitAsync(timedOut.Value!.Id, TimeSpan.FromMilliseconds(100));

        var answered = await _service.RequestAsync(Permission("two?", "wb2"));
        var item = answered.Value!;
        var waiter = _service.WaitAsync(item.Id, TimeSpan.FromSeconds(10));
        await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "wb-r1", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "allow-once", Source = "desktop"
        });
        await waiter.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _service.WaiterBucketCount);
    }

    /// <summary>Concurrent waiters share one bucket and all of them are signalled.</summary>
    [Fact]
    public async Task Wait_SignalsEveryConcurrentWaiterThenReleasesTheBucket()
    {
        var created = await _service.RequestAsync(Permission());
        var item = created.Value!;
        var waiters = Enumerable.Range(0, 8)
            .Select(_ => _service.WaitAsync(item.Id, TimeSpan.FromSeconds(10)))
            .ToArray();
        await Task.Delay(100);

        await _service.RespondAsync(item.Id, new RespondInteractionRequest
        {
            ResponseId = "wb-r2", RequestDigest = item.RequestDigest, Nonce = item.Nonce,
            ChoiceId = "deny", Source = "relay"
        });

        var settled = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(settled, i => Assert.Equal(InteractionStatus.Answered, i!.Status));
        Assert.Equal(0, _service.WaiterBucketCount);
    }

    [Fact]
    public async Task List_FiltersPending()
    {
        await _service.RequestAsync(Permission("one?", "lk1"));
        var two = await _service.RequestAsync(Permission("two?", "lk2"));
        await _service.CancelAsync(two.Value!.Id);
        var pending = await _service.ListAsync(new InteractionQuery { PendingOnly = true });
        Assert.All(pending, i => Assert.Equal(InteractionStatus.Pending, i.Status));
        Assert.Contains(pending, i => i.Key == "lk1");
        Assert.DoesNotContain(pending, i => i.Key == "lk2");
    }
}
