using System.Reflection;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// The web UI's static files, embedded in this assembly so the broker ships as one binary with
/// no content directory to find, lose, or have tampered with.
/// </summary>
internal static class WebUiAssets
{
    private const string Prefix = "AgentNotify.WebUi/";

    private static readonly Lazy<IReadOnlyDictionary<string, Asset>> Files = new(Load);

    public sealed record Asset(byte[] Content, string ContentType);

    public static Asset? Find(string path)
    {
        var normalized = path.Trim('/');
        if (normalized.Length == 0) normalized = "index.html";
        return Files.Value.TryGetValue(normalized, out var asset) ? asset : null;
    }

    public static Asset Index => Find("index.html")
        ?? throw new InvalidOperationException("The web UI assets are missing from this build.");

    private static IReadOnlyDictionary<string, Asset> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            // MSBuild writes RecursiveDir with the build host's separator.
            var relative = name[Prefix.Length..].Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[relative] = new Asset(buffer.ToArray(), ContentType(relative));
        }

        return files;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
