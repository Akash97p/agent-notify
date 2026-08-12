using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Supplies the 256-bit key that protects provider secrets on platforms without DPAPI.
/// </summary>
/// <remarks>
/// Implementations must be idempotent: the first call creates and persists a key, and every later
/// call returns the same key, otherwise previously encrypted provider secrets become unreadable.
/// </remarks>
public interface IMasterKeyStore
{
    /// <summary>Short identifier used in diagnostics, for example <c>macos-keychain</c>.</summary>
    string Name { get; }

    /// <summary>Human-readable description of the protection this store provides.</summary>
    string Description { get; }

    /// <summary>True when this store can be used on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the persisted 32-byte key, creating it on first use.</summary>
    byte[] GetOrCreateKey();
}

/// <summary>Shared helpers for key stores that shell out to a platform credential tool.</summary>
internal static class MasterKeyProcess
{
    internal const int KeyBytes = 32;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a credential tool with an argument list. Arguments are passed directly to the process,
    /// never through a shell, so notification or configuration text can never be interpreted as
    /// command syntax.
    /// </summary>
    internal static (int ExitCode, string StandardOutput) Run(
        string fileName,
        IEnumerable<string> arguments,
        string? standardInput = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEnd();
        // The error stream is drained but deliberately discarded: credential tools echo the
        // requested item back on failure, and it must never reach a log.
        _ = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{fileName} did not exit within {Timeout.TotalSeconds:0} seconds.");
        }

        return (process.ExitCode, output);
    }

    /// <summary>True when <paramref name="tool"/> resolves to an executable on PATH.</summary>
    internal static bool ExistsOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim(), tool))) return true;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }

        return false;
    }

    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>Decodes a stored key, rejecting anything that is not exactly 32 bytes.</summary>
    internal static byte[]? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value.Trim());
            return key.Length == KeyBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Stores the master key in the macOS login keychain through <c>/usr/bin/security</c>.</summary>
public sealed class MacOsKeychainMasterKeyStore : IMasterKeyStore
{
    private const string Tool = "/usr/bin/security";
    private const string Service = "AgentNotify";
    private const string Account = "provider-secrets";

    public string Name => "macos-keychain";

    public string Description => "macOS login keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(Tool);

    public byte[] GetOrCreateKey()
    {
        var existing = MasterKeyProcess.TryDecode(Find());
        if (existing is not null) return existing;

        var key = MasterKeyProcess.NewKey();
        Add(Convert.ToBase64String(key));

        // Read back so a silently rejected write fails now rather than at the first decrypt.
        var stored = MasterKeyProcess.TryDecode(Find())
            ?? throw new CryptographicException("The macOS keychain did not retain the AgentNotify key.");
        return stored;
    }

    private static string? Find()
    {
        var (exitCode, output) = MasterKeyProcess.Run(
            Tool,
            ["find-generic-password", "-s", Service, "-a", Account, "-w"]);
        return exitCode == 0 ? output : null;
    }

    private static void Add(string base64Key)
    {
        // `security` has no stdin path for the secret value, so the key is visible to a process
        // listing for the lifetime of this short-lived call. Switch to stdin if a future release
        // of the tool supports it.
        var (exitCode, _) = MasterKeyProcess.Run(
            Tool,
            ["add-generic-password", "-s", Service, "-a", Account, "-w", base64Key, "-U"]);

        if (exitCode != 0)
            throw new CryptographicException("Could not store the AgentNotify key in the macOS keychain.");
    }
}

/// <summary>Stores the master key in the freedesktop Secret Service through <c>secret-tool</c>.</summary>
public sealed class SecretToolMasterKeyStore : IMasterKeyStore
{
    private const string Tool = "secret-tool";
    private const string Service = "AgentNotify";
    private const string Account = "provider-secrets";

    public string Name => "linux-secret-service";

    public string Description => "Secret Service keyring via secret-tool";

    public bool IsAvailable => OperatingSystem.IsLinux() && MasterKeyProcess.ExistsOnPath(Tool);

    public byte[] GetOrCreateKey()
    {
        var existing = MasterKeyProcess.TryDecode(Lookup());
        if (existing is not null) return existing;

        var key = MasterKeyProcess.NewKey();
        Store(Convert.ToBase64String(key));

        var stored = MasterKeyProcess.TryDecode(Lookup())
            ?? throw new CryptographicException("The Secret Service keyring did not retain the AgentNotify key.");
        return stored;
    }

    private static string? Lookup()
    {
        var (exitCode, output) = MasterKeyProcess.Run(
            Tool,
            ["lookup", "service", Service, "account", Account]);
        return exitCode == 0 ? output : null;
    }

    private static void Store(string base64Key)
    {
        // secret-tool reads the secret from stdin, so the key never appears in the process list.
        var (exitCode, _) = MasterKeyProcess.Run(
            Tool,
            ["store", "--label=AgentNotify provider secrets", "service", Service, "account", Account],
            standardInput: base64Key);

        if (exitCode != 0)
            throw new CryptographicException("Could not store the AgentNotify key in the Secret Service keyring.");
    }
}

/// <summary>
/// Stores the master key in an owner-only file inside the AgentNotify config directory.
/// </summary>
/// <remarks>
/// This is the documented fallback for machines with no usable keyring, and it is deliberately
/// weaker than DPAPI or a keyring: any process running as the same user can read the key file and
/// therefore decrypt stored provider credentials. It is never selected on Windows.
/// </remarks>
public sealed class FileMasterKeyStore : IMasterKeyStore
{
    private readonly string _keyPath;

    public FileMasterKeyStore(string configDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);
        ConfigDir = configDir;
        _keyPath = Path.Combine(configDir, "secret.key");
    }

    public string ConfigDir { get; }

    public string KeyPath => _keyPath;

    public string Name => "file";

    public string Description => "owner-only key file (weaker than a keyring)";

    public bool IsAvailable => true;

    public byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = MasterKeyProcess.TryDecode(File.ReadAllText(_keyPath));
            if (existing is not null)
            {
                UnixFilePermissions.RestrictFile(_keyPath);
                return existing;
            }

            // A truncated or corrupted key file cannot be regenerated without losing every stored
            // provider secret, so fail loudly instead of silently replacing it.
            throw new CryptographicException(
                $"The AgentNotify key file at {_keyPath} is not a valid 256-bit key. " +
                "Restore it from a backup, or delete it and re-enter provider credentials.");
        }

        UnixFilePermissions.CreateOwnerOnlyDirectory(ConfigDir);

        var key = MasterKeyProcess.NewKey();
        var temporary = _keyPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Convert.ToBase64String(key), Encoding.ASCII);
            UnixFilePermissions.RestrictFile(temporary);
            File.Move(temporary, _keyPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Another AgentNotify process created the key first; adopt theirs.
            try { File.Delete(temporary); } catch { }
            return MasterKeyProcess.TryDecode(File.ReadAllText(_keyPath))
                ?? throw new CryptographicException($"The AgentNotify key file at {_keyPath} is unreadable.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }

        UnixFilePermissions.RestrictFile(_keyPath);
        return key;
    }
}
