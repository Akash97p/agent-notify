using System.Reflection;
using System.Text;
using AgentNotify.Core.Harness;

namespace AgentNotify.Cli;

/// <summary>
/// The harness files this build carries, per host.
/// </summary>
/// <remarks>
/// Like <see cref="SkillPayload"/>, the CLI embeds its own copy so
/// <c>install-harness</c> works offline. The install itself lives in
/// <see cref="HarnessInstaller"/>.
/// </remarks>
internal static class HarnessPayload
{
    private const string OpenCodePluginResource = "AgentNotify.Cli.Resources.harness.opencode.agentnotify.js";
    private const string HookScriptResource = "AgentNotify.Cli.Resources.harness.shared.agentnotify_hook.py";

    public static string OpenCodePlugin() => ReadResource(OpenCodePluginResource);

    public static string HookScript() => ReadResource(HookScriptResource);

    private static string ReadResource(string name)
    {
        var assembly = typeof(HarnessPayload).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded harness resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
