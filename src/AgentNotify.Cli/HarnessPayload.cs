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
    private const string OpenClawWatcherResource = "AgentNotify.Cli.Resources.harness.openclaw.agentnotify_openclaw.py";
    private const string HermesPluginYamlResource = "AgentNotify.Cli.Resources.harness.hermes.agentnotify.plugin.yaml";
    private const string HermesPluginInitResource = "AgentNotify.Cli.Resources.harness.hermes.agentnotify.__init__.py";
    private const string PiExtensionResource = "AgentNotify.Cli.Resources.harness.pi.agentnotify.ts";

    public static string OpenCodePlugin() => ReadResource(OpenCodePluginResource);

    public static string HookScript() => ReadResource(HookScriptResource);

    public static string OpenClawWatcher() => ReadResource(OpenClawWatcherResource);

    public static string HermesPluginYaml() => ReadResource(HermesPluginYamlResource);

    public static string HermesPluginInit() => ReadResource(HermesPluginInitResource);

    public static string PiExtension() => ReadResource(PiExtensionResource);

    /// <summary>
    /// The Kilo Code plugin: the OpenCode event plugin retargeted. Kilo speaks the
    /// same V1 plugin/event surface, so only the agent identity and display name change.
    /// Every replacement is asserted: a drifted source file fails loudly instead of
    /// shipping a half-renamed plugin.
    /// </summary>
    public static string KiloPlugin()
    {
        var content = OpenCodePlugin();
        content = ReplaceOrThrow(content,
            "const AGENT = \"opencode\";", "const AGENT = \"kilo\";");
        // Project-name fallbacks when the working directory is unreadable.
        content = ReplaceOrThrow(content,
            "return \"opencode\";", "return \"kilo\";");
        content = ReplaceOrThrow(content,
            ": \"opencode\";", ": \"kilo\";");
        content = ReplaceOrThrow(content,
            "\"--project\", project || \"opencode\",", "\"--project\", project || \"kilo\",");
        content = ReplaceOrThrow(content,
            "OpenCode ready for review", "Kilo ready for review");
        content = ReplaceOrThrow(content,
            "OpenCode session error", "Kilo session error");
        content = ReplaceOrThrow(content,
            "OpenCode waiting for approval", "Kilo waiting for approval");
        content = ReplaceOrThrow(content,
            "OpenCode question for you", "Kilo question for you");
        if (content.Contains("opencode\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The Kilo plugin still references the opencode agent id.");
        return content;
    }

    private static string ReplaceOrThrow(string content, string oldValue, string newValue)
    {
        if (!content.Contains(oldValue, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The harness source no longer contains '{oldValue}'; update the Kilo retargeting.");
        return content.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(HarnessPayload).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded harness resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
