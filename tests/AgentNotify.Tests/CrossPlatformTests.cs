using System.Security.Cryptography;
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
