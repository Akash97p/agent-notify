namespace AgentNotify.Core;

/// <summary>
/// Reduces a configured file name to its final segment identically on every platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path.GetFileName(string)"/> is platform-dependent. Windows treats <c>\</c>, <c>/</c>
/// and the volume separator as boundaries, but on Linux and macOS a backslash is an ordinary
/// character in a file name. A value such as <c>C:\sounds\alert.wav</c> therefore survives
/// <see cref="Path.GetFileName(string)"/> unchanged on Unix, and the invariant that a configured
/// sound is a bare file name inside AgentNotify's managed directory quietly stops holding.
/// </para>
/// <para>
/// Configuration is portable: the same <c>config.json</c> can be copied or synchronized between a
/// Windows machine and a Unix one, so a value written on one platform must normalize the same way
/// on the other. This helper strips both separators and any volume prefix regardless of the
/// operating system. Managed sound file names are content-addressed to
/// <c>[A-Za-z0-9_-]</c> when imported, so no legitimate name is affected by this.
/// </para>
/// </remarks>
public static class SafeFileName
{
    /// <summary>Returns the segment after the last directory or volume separator.</summary>
    public static string Last(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var index = value.AsSpan().LastIndexOfAny('/', '\\');
        var name = index < 0 ? value : value[(index + 1)..];

        // Handles the Windows drive-relative form "C:name.wav", which Windows reduces to
        // "name.wav" but Unix would keep whole.
        var volume = name.LastIndexOf(':');
        return volume < 0 ? name : name[(volume + 1)..];
    }
}
