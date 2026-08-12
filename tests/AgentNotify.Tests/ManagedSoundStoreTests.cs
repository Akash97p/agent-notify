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
    public void SeedBuiltIn_WritesFileWhenAbsent()
    {
        var managed = Path.Combine(_root, "managed");
        var store = new ManagedSoundStore(managed);
        store.SeedBuiltIn("chime.wav", () => new MemoryStream([9, 8, 7]));
        var path = Path.Combine(managed, "chime.wav");
        Assert.True(File.Exists(path));
        Assert.Equal([9, 8, 7], File.ReadAllBytes(path));
    }

    [Fact]
    public void SeedBuiltIn_DoesNotOverwriteExistingFile()
    {
        var managed = Path.Combine(_root, "managed");
        Directory.CreateDirectory(managed);
        var path = Path.Combine(managed, "chime.wav");
        File.WriteAllBytes(path, [1, 1, 1]);
        var store = new ManagedSoundStore(managed);
        store.SeedBuiltIn("chime.wav", () => new MemoryStream([2, 2, 2]));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(path));
    }

    [Fact]
    public void SeedBuiltIn_IsSafeToCallTwice()
    {
        var managed = Path.Combine(_root, "managed");
        var store = new ManagedSoundStore(managed);
        store.SeedBuiltIn("ping.wav", () => new MemoryStream([5, 6]));
        store.SeedBuiltIn("ping.wav", () => new MemoryStream([7, 8]));
        var path = Path.Combine(managed, "ping.wav");
        Assert.Equal([5, 6], File.ReadAllBytes(path));
    }

    [Fact]
    public void BuiltInTones_HasExpectedEntriesAndContainsBehaviour()
    {
        Assert.Equal(4, BuiltInTones.All.Count);
        Assert.Equal("chime.wav", BuiltInTones.All[0].FileName);
        Assert.Equal("Chime", BuiltInTones.All[0].DisplayName);
        Assert.Equal("ping.wav", BuiltInTones.All[1].FileName);
        Assert.Equal("Ping", BuiltInTones.All[1].DisplayName);
        Assert.Equal("alert.wav", BuiltInTones.All[2].FileName);
        Assert.Equal("Alert", BuiltInTones.All[2].DisplayName);
        Assert.Equal("knock.wav", BuiltInTones.All[3].FileName);
        Assert.Equal("Knock", BuiltInTones.All[3].DisplayName);
        Assert.True(BuiltInTones.Contains("chime.wav"));
        Assert.True(BuiltInTones.Contains("CHIME.WAV"));
        Assert.True(BuiltInTones.Contains("a/b/chime.wav"));
        Assert.True(BuiltInTones.Contains("PING.wav"));
        Assert.False(BuiltInTones.Contains(null));
        Assert.False(BuiltInTones.Contains(""));
        Assert.False(BuiltInTones.Contains("   "));
        Assert.False(BuiltInTones.Contains("unknown.wav"));
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
