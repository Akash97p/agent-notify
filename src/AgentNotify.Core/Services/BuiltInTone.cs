namespace AgentNotify.Core.Services;

public sealed record BuiltInTone(string FileName, string DisplayName);

public static class BuiltInTones
{
    public static IReadOnlyList<BuiltInTone> All { get; } =
    [
        new("chime.wav", "Chime"),
        new("ping.wav", "Ping"),
        new("alert.wav", "Alert"),
        new("knock.wav", "Knock"),
    ];

    public static bool Contains(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = Path.GetFileName(fileName);
        return All.Any(t => string.Equals(t.FileName, name, StringComparison.OrdinalIgnoreCase));
    }
}
