using System.Security.Cryptography;
using AgentNotify.Contracts;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Tests;

/// <summary>
/// Covers the portable pieces added so the broker can run outside Windows: the shared adapter
/// list, the platform secret-protection selection, and owner-only local state on Unix.
/// </summary>
public sealed class CrossPlatformTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"an-xplat-{Guid.NewGuid():N}");

    public CrossPlatformTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AdapterFactory_CreatesEveryImplementedAdapter()
    {
        var adapters = ChannelAdapterFactory.CreateAll();

        Assert.Equal(18, adapters.Count);
        Assert.All(adapters, adapter => Assert.False(string.IsNullOrWhiteSpace(adapter.Kind)));

        var kinds = adapters.Select(adapter => adapter.Kind).ToArray();
        Assert.Equal(kinds.Length, kinds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AdapterFactory_ReturnsIndependentInstances()
    {
        var first = ChannelAdapterFactory.CreateAll();
        var second = ChannelAdapterFactory.CreateAll();

        // Adapters hold their own HttpClient; sharing instances across dispatchers would let one
        // disposal break another.
        Assert.All(first.Zip(second), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public void FileMasterKeyStore_CreatesAndThenReusesOneKey()
    {
        var store = new FileMasterKeyStore(_dir);

        var created = store.GetOrCreateKey();
        var reloaded = new FileMasterKeyStore(_dir).GetOrCreateKey();

        Assert.Equal(32, created.Length);
        Assert.Equal(created, reloaded);
        Assert.True(File.Exists(store.KeyPath));
    }

    [Fact]
    public void FileMasterKeyStore_RejectsCorruptedKeyRatherThanReplacingIt()
    {
        var store = new FileMasterKeyStore(_dir);
        store.GetOrCreateKey();
        File.WriteAllText(store.KeyPath, "not-a-key");

        // Silently regenerating would make every stored provider secret undecryptable.
        Assert.Throws<CryptographicException>(() => new FileMasterKeyStore(_dir).GetOrCreateKey());
    }

    [Fact]
    public void SecretRoundTripsThroughTheFileBackedProtector()
    {
        var key = new FileMasterKeyStore(_dir).GetOrCreateKey();
        var protector = new AesGcmSecretProtector(key);

        var envelope = protector.Protect("bot-token-value");

        Assert.DoesNotContain("bot-token-value", envelope, StringComparison.Ordinal);
        Assert.Equal("bot-token-value", protector.Unprotect(envelope));

        // A fresh protector built from the persisted key must decrypt the same envelope.
        var reloaded = new AesGcmSecretProtector(new FileMasterKeyStore(_dir).GetOrCreateKey());
        Assert.Equal("bot-token-value", reloaded.Unprotect(envelope));
    }

    [Fact]
    public void ProtectorFactory_SelectsTheExpectedProtectionForThisPlatform()
    {
        var protector = SecretProtectorFactory.Create(_dir, logger: null, out var protection);

        if (OperatingSystem.IsWindows())
        {
            // Windows must never fall back to a key file while DPAPI is available.
            Assert.IsType<DpapiSecretProtector>(protector);
            Assert.Equal("dpapi", protection.StoreName);
            Assert.True(protection.IsUserBound);
            Assert.False(File.Exists(Path.Combine(_dir, "secret.key")));
        }
        else
        {
            Assert.IsType<AesGcmSecretProtector>(protector);
            Assert.NotEqual("dpapi", protection.StoreName);
        }

        Assert.Equal("secret", protector.Unprotect(protector.Protect("secret")));
    }

    [Fact]
    public void ConfigStore_WritesTheTokenFileOwnerOnlyOnUnix()
    {
        var store = new ConfigStore(_dir, applyEnvOverrides: false);
        var config = new AgentNotifyConfig();
        store.EnsureAuthToken(config);
        store.Save(config);

        Assert.True(File.Exists(store.ConfigPath));

        if (OperatingSystem.IsWindows())
        {
            // Unix modes are not meaningful here; the per-user profile ACL protects the token.
            Assert.False(UnixFilePermissions.IsSupported);
            return;
        }

        var mode = File.GetUnixFileMode(store.ConfigPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        var directoryMode = File.GetUnixFileMode(_dir);
        Assert.False(directoryMode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(directoryMode.HasFlag(UnixFileMode.OtherRead));
    }

    [Theory]
    // Windows-style input must normalize the same way on Linux and macOS, where a backslash is an
    // ordinary file-name character and Path.GetFileName would let the whole value through.
    [InlineData(@"C:\outside\global.MP3", "global.MP3")]
    [InlineData(@"..\attention.wav", "attention.wav")]
    [InlineData(@"\\server\share\alert.wav", "alert.wav")]
    [InlineData("C:global.mp3", "global.mp3")]
    // Unix-style input, which Path.GetFileName already handles on both platforms.
    [InlineData("/etc/passwd.wav", "passwd.wav")]
    [InlineData("../../attention.wav", "attention.wav")]
    // Already-bare names are returned unchanged.
    [InlineData("chime.wav", "chime.wav")]
    [InlineData("", "")]
    public void SafeFileName_NormalizesIdenticallyOnEveryPlatform(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Last(input));
    }

    [Fact]
    public void ConfiguredSoundFile_NeverKeepsDirectoryComponents()
    {
        var config = new AgentNotifyConfig
        {
            DefaultSoundFile = @"C:\outside\global.MP3",
            TypeSoundFiles = new() { ["input_required"] = @"..\attention.wav" }
        };

        config.ApplyDefaults();

        // A config file authored on Windows can be copied to a Unix machine, so the stored value
        // must reduce to a bare file name inside the managed sounds directory on both.
        Assert.Equal("global.MP3", config.DefaultSoundFile);
        Assert.Equal("attention.wav", config.SoundFileFor(NotificationTypes.InputRequired));
        Assert.DoesNotContain('\\', config.DefaultSoundFile!);
        Assert.DoesNotContain('/', config.DefaultSoundFile!);
    }

    [Fact]
    public void DefaultConfigDir_IsAlwaysAbsolute()
    {
        var dir = ConfigStore.DefaultConfigDir();

        // A relative path here would put the bearer token, the secret key, and the history
        // database in whatever directory the broker was started from. On Unix that is the
        // repository an agent is working in.
        Assert.True(Path.IsPathRooted(dir), $"Config directory '{dir}' must be absolute.");
        Assert.EndsWith("AgentNotify", dir, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetFullPath("AgentNotify"), dir);
    }

    [Fact]
    public void ConfigStore_LeavesNoTemporaryFileBehind()
    {
        var store = new ConfigStore(_dir, applyEnvOverrides: false);
        store.Save(new AgentNotifyConfig());

        Assert.Empty(Directory.GetFiles(_dir, "config.json.tmp-*"));
    }
}
