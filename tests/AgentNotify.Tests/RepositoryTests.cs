using AgentNotify.Contracts;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Persistence;

namespace AgentNotify.Tests;

public sealed class RepositoryTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteNotificationRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"an-repo-{Guid.NewGuid():N}.db");
        _repo = new SqliteNotificationRepository(_dbPath);
        await _repo.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAndGet()
    {
        var created = await _repo.CreateAsync(Make("hello"));
        var fetched = await _repo.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("hello", fetched!.Title);
        Assert.Equal(NotificationStatus.Active, fetched.Status);
    }

    [Fact]
    public async Task Query_Filters()
    {
        await _repo.CreateAsync(Make("a", agent: "alpha", type: NotificationType.Info));
        await _repo.CreateAsync(Make("b", agent: "beta", type: NotificationType.Error));
        var onlyAlpha = await _repo.QueryAsync(new NotificationQuery { Agent = "alpha" });
        Assert.Single(onlyAlpha);
        Assert.Equal("alpha", onlyAlpha[0].Agent);

        var onlyError = await _repo.QueryAsync(new NotificationQuery { Type = NotificationType.Error });
        Assert.Single(onlyError);
        Assert.Equal(NotificationType.Error, onlyError[0].Type);
    }

    [Fact]
    public async Task Query_Unresolved()
    {
        var n = await _repo.CreateAsync(Make("active"));
        await _repo.UpdateStatusAsync(n.Id, NotificationStatus.Resolved, DateTimeOffset.UtcNow);
        await _repo.CreateAsync(Make("active2"));

        var unresolved = await _repo.QueryAsync(new NotificationQuery { Unresolved = true });
        Assert.All(unresolved, x => Assert.Equal(NotificationStatus.Active, x.Status));
        var all = await _repo.QueryAsync(new NotificationQuery());
        Assert.True(all.Count >= 2);
        Assert.True(unresolved.Count < all.Count);
    }

    [Fact]
    public async Task Query_Limit()
    {
        for (var i = 0; i < 5; i++) await _repo.CreateAsync(Make($"t{i}"));
        var limited = await _repo.QueryAsync(new NotificationQuery { Limit = 2 });
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task CountActive()
    {
        var before = await _repo.CountActiveAsync();
        await _repo.CreateAsync(Make("x"));
        var after = await _repo.CountActiveAsync();
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task UpdateStatus()
    {
        var n = await _repo.CreateAsync(Make("to dismiss"));
        var updated = await _repo.UpdateStatusAsync(n.Id, NotificationStatus.Dismissed, DateTimeOffset.UtcNow);
        Assert.NotNull(updated);
        Assert.Equal(NotificationStatus.Dismissed, updated!.Status);
        Assert.NotNull(updated.ResolvedAt);
    }

    [Fact]
    public async Task FindActiveByKey()
    {
        var a = await _repo.CreateAsync(Make("k1", key: "my-key"));
        var found = await _repo.FindActiveByKeyAsync("my-key");
        Assert.NotNull(found);
        Assert.Equal(a.Id, found!.Id);

        await _repo.UpdateStatusAsync(a.Id, NotificationStatus.Resolved, DateTimeOffset.UtcNow);
        var after = await _repo.FindActiveByKeyAsync("my-key");
        Assert.Null(after);
    }

    [Fact]
    public async Task Update_Full()
    {
        var n = await _repo.CreateAsync(Make("orig"));
        n.Title = "updated";
        n.Message = "new msg";
        var updated = await _repo.UpdateAsync(n);
        Assert.NotNull(updated);
        Assert.Equal("updated", updated!.Title);
        Assert.Equal("new msg", updated.Message);
    }

    [Fact]
    public async Task Prune()
    {
        var n = await _repo.CreateAsync(Make("old dismissed"));
        await _repo.UpdateStatusAsync(n.Id, NotificationStatus.Dismissed, DateTimeOffset.UtcNow.AddDays(-10));
        var pruned = await _repo.PruneAsync(DateTimeOffset.UtcNow.AddDays(-5));
        Assert.Equal(1, pruned);
        Assert.Null(await _repo.GetByIdAsync(n.Id));
    }

    [Fact]
    public async Task Prune_DoesNotRemoveActive()
    {
        await _repo.CreateAsync(Make("active stays"));
        var pruned = await _repo.PruneAsync(DateTimeOffset.UtcNow.AddDays(-5));
        Assert.Equal(0, pruned);
    }

    private static Notification Make(string title, string? key = null, string agent = "test",
        NotificationType type = NotificationType.Info) =>
        Notification.Create(new CreateNotificationRequest
        {
            Title = title,
            Message = "msg " + title,
            Agent = agent,
            Type = type,
            Key = key
        }, DateTimeOffset.UtcNow);
}
