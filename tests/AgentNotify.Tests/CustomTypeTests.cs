using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;

namespace AgentNotify.Tests;

public sealed class CustomTypeTests
{
    [Fact]
    public async Task Service_UsesCustomDefaultPriorityAndPersistsIdentifier()
    {
        var db = Path.Combine(Path.GetTempPath(), $"an-custom-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteNotificationRepository(db);
            await repo.InitializeAsync();
            var config = new AgentNotifyConfig
            {
                CustomNotificationTypes = [new() { Id = "release_wait", DefaultPriority = NotificationPriority.Critical }]
            };
            config.ApplyDefaults();
            var service = new NotificationService(repo, config);
            var result = await service.CreateAsync(new CreateNotificationRequest
            {
                Type = "release-wait", Title = "Waiting", Message = "Approve release"
            });
            Assert.Null(result.Error);
            Assert.Equal("release_wait", result.Value!.Type);
            Assert.Equal(NotificationPriority.Critical, result.Value.Priority);
            Assert.Equal("release_wait", (await repo.GetByIdAsync(result.Value.Id))!.Type);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) try { File.Delete(db + suffix); } catch { }
        }
    }
}

