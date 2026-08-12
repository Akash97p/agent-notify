using AgentNotify.Core.Logging;

namespace AgentNotify.Core.Delivery;

/// <summary>The secret protection actually in use, for diagnostics and honest UI reporting.</summary>
/// <param name="StoreName">Short identifier, for example <c>dpapi</c> or <c>macos-keychain</c>.</param>
/// <param name="Description">Human-readable description shown to the user.</param>
/// <param name="IsUserBound">
/// True when the operating system binds the protection to the signed-in user, so another local user
/// cannot decrypt the stored secrets. False for the key-file fallback.
/// </param>
public sealed record SecretProtection(string StoreName, string Description, bool IsUserBound);

/// <summary>
/// Chooses how provider secrets are encrypted before they reach SQLite. Windows always uses DPAPI;
/// other platforms use AES-GCM under a key held by the platform keyring, or an owner-only key file
/// when no keyring is available.
/// </summary>
public static class SecretProtectorFactory
{
    /// <summary>Creates the protector for the current platform.</summary>
    /// <param name="configDir">The AgentNotify per-user config directory.</param>
    /// <param name="logger">Optional logger; the key is never written to it.</param>
    public static ISecretProtector Create(string configDir, IAppLogger? logger = null) =>
        Create(configDir, logger, out _);

    /// <summary>
    /// Creates the protector for the current platform and reports which protection was selected.
    /// </summary>
    public static ISecretProtector Create(string configDir, IAppLogger? logger, out SecretProtection protection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);

        if (OperatingSystem.IsWindows())
        {
            protection = new SecretProtection("dpapi", "Windows DPAPI, current user", IsUserBound: true);
            logger?.Info("Provider secrets are protected with Windows DPAPI (current user).");
            return new DpapiSecretProtector();
        }

        foreach (var store in PlatformKeyStores(configDir))
        {
            if (!store.IsAvailable) continue;

            try
            {
                var key = store.GetOrCreateKey();
                protection = new SecretProtection(store.Name, store.Description, IsUserBound: true);
                logger?.Info($"Provider secrets are protected with AES-GCM under the {store.Description}.");
                return new AesGcmSecretProtector(key);
            }
            catch (Exception exception)
            {
                // A locked or unavailable keyring must not stop AgentNotify from starting; fall
                // through to the next store. The exception message can echo the requested item, so
                // only the store name is recorded.
                logger?.Warn($"Secret key store '{store.Name}' is unusable ({exception.GetType().Name}); trying the next one.");
            }
        }

        var fallback = new FileMasterKeyStore(configDir);
        var fallbackKey = fallback.GetOrCreateKey();
        protection = new SecretProtection(fallback.Name, fallback.Description, IsUserBound: false);
        logger?.Warn(
            "No platform keyring is available. Provider secrets are protected with AES-GCM under an " +
            $"owner-only key file at {fallback.KeyPath}. Any process running as this user can read it.");
        return new AesGcmSecretProtector(fallbackKey);
    }

    private static IEnumerable<IMasterKeyStore> PlatformKeyStores(string configDir)
    {
        if (OperatingSystem.IsMacOS()) yield return new MacOsKeychainMasterKeyStore();
        if (OperatingSystem.IsLinux()) yield return new SecretToolMasterKeyStore();
        _ = configDir;
    }
}
