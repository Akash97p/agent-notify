using AgentNotify.Core.Services;
using AgentNotify.Core.Config;
using AgentNotify.Contracts;

namespace AgentNotify.Tests;

public sealed class ManagedSoundStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "an-sounds-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Import_CopiesAllowedFileWithSafeContentAddressedName()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "my alert (final).MP3");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var managed = Path.Combine(_root, "managed");
        var store = new ManagedSoundStore(managed);
        var first = store.Import(source);
        var second = store.Import(source);
        Assert.Equal(first, second);
        Assert.EndsWith(".mp3", first);
        Assert.DoesNotContain(' ', first);
        Assert.Equal(Path.Combine(managed, first), store.Resolve(first));
        Assert.Single(Directory.GetFiles(managed));
    }

    [Fact]
    public void Import_RejectsUnsupportedAndEmptyFiles()
    {
        Directory.CreateDirectory(_root);
        var text = Path.Combine(_root, "bad.txt"); File.WriteAllText(text, "data");
        var empty = Path.Combine(_root, "empty.wav"); File.WriteAllBytes(empty, []);
        var store = new ManagedSoundStore(Path.Combine(_root, "managed"));
        Assert.Throws<InvalidOperationException>(() => store.Import(text));
        Assert.Throws<InvalidOperationException>(() => store.Import(empty));
    }

    [Fact]
    public void Resolve_CannotEscapeManagedDirectory()
    {
        var store = new ManagedSoundStore(Path.Combine(_root, "managed"));
        Assert.Null(store.Resolve("..\\secret.mp3"));
    }

    [Fact]
    public void SoundPolicy_RespectsPauseDndAndCriticalOverride()
    {
        var config = new AgentNotifyConfig { SoundsEnabled = true, DoNotDisturb = true };
        Assert.False(NotificationSoundPolicy.ShouldPlay(config, NotificationPriority.Critical));
        config.PlayCriticalSoundsDuringDoNotDisturb = true;
        Assert.True(NotificationSoundPolicy.ShouldPlay(config, NotificationPriority.Critical));
        Assert.False(NotificationSoundPolicy.ShouldPlay(config, NotificationPriority.High));
        config.PauseNotifications = true;
        Assert.False(NotificationSoundPolicy.ShouldPlay(config, NotificationPriority.Critical));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
