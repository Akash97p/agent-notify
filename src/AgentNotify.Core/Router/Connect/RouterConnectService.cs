using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentNotify.Core.Router.Connect;

/// <summary>
/// Connects and disconnects the agents on this machine, so a routed model appears in the agent's own
/// model picker instead of having to be typed.
/// </summary>
/// <remarks>
/// This writes files that belong to other programs, so three rules hold everywhere in this class.
/// A copy of the file is taken before every change and kept, and the owner can restore any copy from
/// the Router page. Only AgentNotify's own lines or keys are touched; everything else in the file
/// survives byte for byte. Nothing is written for an agent that is not installed.
///
/// The generated Codex catalogue is rewritten whenever the router's upstreams, routes, or key change,
/// because a picker listing models that no longer resolve is worse than no picker at all.
/// </remarks>
public sealed class RouterConnectService
{
    public const int MaxBackupsPerAgent = 10;

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RouterConfigService _router;
    private readonly string _stateDir;
    private readonly string _statePath;
    private readonly CodexRouterConnector _codex;
    private readonly ClaudeCodeRouterConnector _claude;
    private readonly TimeProvider _clock;
    private readonly Func<int> _port;
    private readonly object _gate = new();

    /// <param name="home">The home directory the agents' configuration lives under; tests pass their own.</param>
    /// <param name="port">Read each time, because the broker's port can change without a restart of this service.</param>
    public RouterConnectService(
        RouterConfigService router,
        string stateDir,
        Func<int> port,
        string? home = null,
        TimeProvider? clock = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _stateDir = stateDir ?? throw new ArgumentNullException(nameof(stateDir));
        _statePath = Path.Combine(_stateDir, "agent-connections.json");
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _clock = clock ?? TimeProvider.System;
        var root = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _codex = new CodexRouterConnector(Path.Combine(root, ".codex"));
        _claude = new ClaudeCodeRouterConnector(Path.Combine(root, ".claude"));
    }

    /// <summary>The base URL an OpenAI-wire client is given.</summary>
    public string OpenAiBaseUrl => $"http://127.0.0.1:{_port()}/router/v1";

    /// <summary>The base URL Claude Code is given; it appends <c>/v1/messages</c> itself.</summary>
    public string AnthropicBaseUrl => $"http://127.0.0.1:{_port()}/router";

    public string CatalogPath => Path.Combine(_stateDir, CodexModelCatalog.FileName);

    public async Task<IReadOnlyList<RouterAgentInfo>> ListAsync(CancellationToken ct = default)
    {
        var snapshot = await _router.GetSnapshotAsync(ct).ConfigureAwait(false);
        var states = LoadStates();
        return [Describe(CodexRouterConnector.Id, snapshot, states), Describe(ClaudeCodeRouterConnector.Id, snapshot, states)];
    }

    public async Task<RouterConnectResult> ConnectAsync(string agentId, RouterConnectRequest request, CancellationToken ct = default)
    {
        var snapshot = await _router.GetSnapshotAsync(ct).ConfigureAwait(false);
        var key = await _router.GetRouterKeyAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Turn the router on first: it has no key yet.");

        var selectable = Selectable(snapshot);
        if (selectable.Count == 0)
            throw new InvalidOperationException("Add an enabled upstream with at least one model before connecting an agent.");

        var states = LoadStates();
        var state = states.TryGetValue(agentId, out var existing) ? existing : new RouterAgentState { Id = agentId };

        switch (agentId)
        {
            case CodexRouterConnector.Id:
            {
                Require(_codex.Detected, "Codex is not installed for this user: no ~/.codex directory was found.");
                var model = Validate(request.Model, selectable, "model");
                var options = ValidateOptions(request.Options, selectable, CodexRouterConnector.Options);
                // The shell tool belongs to the generated catalogue rather than to config.toml.
                options.TryGetValue("shell_tool", out var shellTool);
                var catalogCount = CodexModelCatalog.Write(CatalogPath, snapshot, shellType: shellTool);
                if (catalogCount == 0)
                    throw new InvalidOperationException("The router has no selectable model, so Codex would refuse the catalogue.");
                Backup(_codex.ConfigPath, state, state.ConnectedAt is null ? "before connecting" : "before reconnecting");
                var previous = _codex.Apply(OpenAiBaseUrl, key, CatalogPath, model, options);
                state = state with
                {
                    Id = agentId,
                    ConnectedAt = _clock.GetUtcNow(),
                    SelectedModel = model,
                    Options = new Dictionary<string, string>(options, StringComparer.Ordinal),
                    Previous = Merge(state, previous)
                };
                break;
            }

            case ClaudeCodeRouterConnector.Id:
            {
                Require(_claude.Detected, "Claude Code is not installed for this user: no ~/.claude directory was found.");
                var slots = new Dictionary<string, string>(StringComparer.Ordinal);
                var requested = request.ModelSlots ?? new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var slot in ClaudeCodeRouterConnector.Slots.Keys)
                {
                    if (requested.TryGetValue(slot, out var value) && !string.IsNullOrWhiteSpace(value))
                        slots[slot] = Validate(value, selectable, $"model for the {slot} slot");
                }

                // The default slot is what an unconfigured session sends, so it always gets a value.
                if (!slots.ContainsKey("default"))
                    slots["default"] = Validate(request.Model, selectable, "model");

                var claudeOptions = ValidateOptions(request.Options, selectable, ClaudeCodeRouterConnector.Options);
                Backup(_claude.ConfigPath, state, state.ConnectedAt is null ? "before connecting" : "before reconnecting");
                var previous = _claude.Apply(AnthropicBaseUrl, key, slots, PickerRows(snapshot), claudeOptions);
                state = state with
                {
                    Id = agentId,
                    ConnectedAt = _clock.GetUtcNow(),
                    SelectedModel = slots["default"],
                    ModelSlots = new Dictionary<string, string>(slots, StringComparer.Ordinal),
                    Options = new Dictionary<string, string>(claudeOptions, StringComparer.Ordinal),
                    Previous = Merge(state, previous)
                };
                break;
            }

            default:
                throw new KeyNotFoundException($"No connector for agent '{agentId}'.");
        }

        states[agentId] = state;
        SaveStates(states);
        return new RouterConnectResult(Describe(agentId, snapshot, states), $"{DisplayName(agentId)} now routes through AgentNotify.");
    }

    public async Task<RouterConnectResult> DisconnectAsync(string agentId, CancellationToken ct = default)
    {
        var snapshot = await _router.GetSnapshotAsync(ct).ConfigureAwait(false);
        var states = LoadStates();
        var state = states.TryGetValue(agentId, out var existing) ? existing : new RouterAgentState { Id = agentId };

        switch (agentId)
        {
            case CodexRouterConnector.Id:
                Backup(_codex.ConfigPath, state, "before disconnecting");
                _codex.Remove(state.Previous);
                break;
            case ClaudeCodeRouterConnector.Id:
                Backup(_claude.ConfigPath, state, "before disconnecting");
                _claude.Remove(state.Previous);
                break;
            default:
                throw new KeyNotFoundException($"No connector for agent '{agentId}'.");
        }

        states[agentId] = state with
        {
            Id = agentId,
            ConnectedAt = null,
            SelectedModel = null,
            ModelSlots = new Dictionary<string, string>(StringComparer.Ordinal),
            Previous = new Dictionary<string, string?>(StringComparer.Ordinal)
        };
        SaveStates(states);
        return new RouterConnectResult(
            Describe(agentId, snapshot, states),
            $"{DisplayName(agentId)} no longer routes through AgentNotify. Its own settings were put back.");
    }

    /// <summary>Copies one kept backup back over the agent's configuration file.</summary>
    public async Task<RouterConnectResult> RestoreAsync(string agentId, string backupId, CancellationToken ct = default)
    {
        var snapshot = await _router.GetSnapshotAsync(ct).ConfigureAwait(false);
        var states = LoadStates();
        if (!states.TryGetValue(agentId, out var state))
            throw new KeyNotFoundException("AgentNotify has no saved copy for that agent.");

        var backup = state.Backups.FirstOrDefault(b => b.Id == backupId)
            ?? throw new KeyNotFoundException("That saved copy was not found.");
        if (!File.Exists(backup.Path))
            throw new FileNotFoundException("That saved copy is no longer on disk.", backup.Path);

        var target = ConfigPathFor(agentId);
        // The file about to be overwritten is itself worth keeping: a restore is an easy thing to
        // regret, and this way the owner can step back and forth.
        Backup(target, state, "before restoring a saved copy");
        File.Copy(backup.Path, target, overwrite: true);
        UnixFilePermissions.RestrictFile(target);

        var connected = agentId == CodexRouterConnector.Id ? _codex.IsConnected() : _claude.IsConnected();
        states[agentId] = state with
        {
            ConnectedAt = connected ? state.ConnectedAt ?? _clock.GetUtcNow() : null,
            SelectedModel = connected ? state.SelectedModel : null,
            ModelSlots = connected ? state.ModelSlots : new Dictionary<string, string>(StringComparer.Ordinal)
        };
        SaveStates(states);
        return new RouterConnectResult(
            Describe(agentId, snapshot, states),
            $"Restored {DisplayName(agentId)}'s configuration from the copy taken {backup.CreatedAt.ToLocalTime():f}.");
    }

    /// <summary>
    /// Rewrites what a connected agent depends on: the Codex catalogue and the embedded key. Called
    /// after any router change, and safe to call when nothing is connected.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var states = LoadStates();
        if (states.Count == 0) return;
        var snapshot = await _router.GetSnapshotAsync(ct).ConfigureAwait(false);
        var key = await _router.GetRouterKeyAsync(ct).ConfigureAwait(false);
        if (key is null) return;

        if (states.TryGetValue(CodexRouterConnector.Id, out var codexState) &&
            codexState.ConnectedAt is not null && _codex.IsConnected())
        {
            var selectable = Selectable(snapshot);
            var model = codexState.SelectedModel is { Length: > 0 } chosen && selectable.Contains(chosen)
                ? chosen
                : selectable.FirstOrDefault();
            codexState.Options.TryGetValue("shell_tool", out var shellTool);
            if (model is not null && CodexModelCatalog.Write(CatalogPath, snapshot, shellType: shellTool) > 0)
            {
                // A subagent or review model that no longer resolves is dropped rather than written
                // back: Codex would otherwise send a selector this router refuses.
                var options = codexState.Options
                    .Where(pair => !IsModelOption(pair.Key) || selectable.Contains(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                _codex.Apply(OpenAiBaseUrl, key, CatalogPath, model, options);
                if (!string.Equals(model, codexState.SelectedModel, StringComparison.Ordinal))
                {
                    states[CodexRouterConnector.Id] = codexState with { SelectedModel = model };
                    SaveStates(states);
                }
            }
        }

        if (states.TryGetValue(ClaudeCodeRouterConnector.Id, out var claudeState) &&
            claudeState.ConnectedAt is not null && _claude.IsConnected())
        {
            var slots = claudeState.ModelSlots.Count > 0
                ? claudeState.ModelSlots
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["default"] = claudeState.SelectedModel ?? "" };
            _claude.Apply(AnthropicBaseUrl, key, slots, PickerRows(snapshot), claudeState.Options);
        }
    }

    /// <summary>Every selector an agent can be pointed at, in picker order.</summary>
    public static IReadOnlyList<string> Selectable(RouterSnapshot snapshot)
    {
        var selectors = new List<string>();
        foreach (var route in snapshot.Routes.Where(r => r.Enabled))
        {
            selectors.Add(route.Name);
            if (route.Kind == RouterKind.Combo) selectors.Add("combo/" + route.Name);
        }
        foreach (var upstream in snapshot.Upstreams.Where(u => u.Enabled))
            foreach (var model in upstream.Models)
                selectors.Add(upstream.Slug + "/" + model);
        return selectors;
    }

    private RouterAgentInfo Describe(string agentId, RouterSnapshot snapshot, Dictionary<string, RouterAgentState> states)
    {
        states.TryGetValue(agentId, out var state);
        var backups = (state?.Backups ?? []).Where(b => File.Exists(b.Path))
            .OrderByDescending(b => b.CreatedAt).ToList();
        var hasModels = Selectable(snapshot).Count > 0;

        if (agentId == CodexRouterConnector.Id)
        {
            var connected = _codex.IsConnected();
            return new RouterAgentInfo(
                CodexRouterConnector.Id, _codex.DisplayName, _codex.ConfigPath, _codex.Detected, connected,
                connected ? state?.ConnectedAt : null,
                connected ? state?.SelectedModel : null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                CatalogPath, CodexModelCatalog.CountAt(CatalogPath), backups,
                Blocked(_codex.Detected, hasModels, "Codex", "~/.codex"))
            {
                Options = WithSelectors(CodexRouterConnector.Options, snapshot),
                OptionValues = connected ? CodexOptionValues(state) : new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        var claudeConnected = _claude.IsConnected();
        return new RouterAgentInfo(
            ClaudeCodeRouterConnector.Id, _claude.DisplayName, _claude.ConfigPath, _claude.Detected, claudeConnected,
            claudeConnected ? state?.ConnectedAt : null,
            claudeConnected ? state?.SelectedModel : null,
            _claude.CurrentSlots(), null, 0, backups,
            Blocked(_claude.Detected, hasModels, "Claude Code", "~/.claude"))
        {
            Options = WithSelectors(ClaudeCodeRouterConnector.Options, snapshot),
            OptionValues = claudeConnected ? _claude.CurrentOptions() : new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// The rows added to a host's own model list: one per selector, described well enough that the
    /// owner can tell a combo from a single provider without opening this interface.
    /// </summary>
    private static IReadOnlyList<(string Selector, string Description)> PickerRows(RouterSnapshot snapshot)
    {
        var rows = new List<(string, string)>();
        foreach (var route in snapshot.Routes.Where(r => r.Enabled))
        {
            var kind = route.Kind == RouterKind.Combo ? "failover combo" : "alias";
            var describes = string.Join(", ", route.Targets);
            rows.Add((route.Name, $"Router {kind}: {describes}"));
            if (route.Kind == RouterKind.Combo)
                rows.Add(("combo/" + route.Name, $"Router failover combo: {describes}"));
        }
        foreach (var upstream in snapshot.Upstreams.Where(u => u.Enabled))
            foreach (var model in upstream.Models)
                rows.Add(($"{upstream.Slug}/{model}", $"Routed to {upstream.Label} as {model}"));
        return rows;
    }

    /// <summary>
    /// What the Codex options are set to now. Most are read back from config.toml; the shell tool is
    /// not written there, so it comes from AgentNotify's own record of the last connect.
    /// </summary>
    private Dictionary<string, string> CodexOptionValues(RouterAgentState? state)
    {
        var values = new Dictionary<string, string>(_codex.CurrentOptions(), StringComparer.Ordinal);
        values["shell_tool"] = state is not null && state.Options.TryGetValue("shell_tool", out var shell)
            ? shell
            : CodexModelCatalog.DefaultShellType;
        return values;
    }

    /// <summary>Fills each model-valued option's choices with the selectors this router serves.</summary>
    private static IReadOnlyList<RouterAgentOption> WithSelectors(
        IReadOnlyList<RouterAgentOption> options,
        RouterSnapshot snapshot)
    {
        var selectors = Selectable(snapshot);
        return options
            .Select(option => option.IsModelSelector ? option with { Choices = selectors } : option)
            .ToList();
    }

    private static bool IsModelOption(string option) =>
        CodexRouterConnector.Options.Any(o => o.Id == option && o.IsModelSelector);

    /// <summary>
    /// Keeps only options the connector declares, and holds each to its own kind: a model-valued one
    /// must name a selector this router serves, and a fixed-choice one must be one of those choices.
    /// </summary>
    private static Dictionary<string, string> ValidateOptions(
        IReadOnlyDictionary<string, string>? requested,
        IReadOnlyList<string> selectable,
        IReadOnlyList<RouterAgentOption> declared)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requested is null) return result;

        foreach (var (id, raw) in requested)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            var option = declared.FirstOrDefault(o => o.Id == id)
                ?? throw new ArgumentException($"'{id}' is not a setting this agent has.");
            if (option.IsModelSelector)
            {
                if (!selectable.Contains(value, StringComparer.Ordinal))
                    throw new ArgumentException($"The {option.DisplayName.ToLowerInvariant()} '{value}' is not a selector this router serves.");
            }
            else if (option.Choices.Count > 0 && !option.Choices.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"'{value}' is not a value for {option.DisplayName.ToLowerInvariant()}. Choose one of: {string.Join(", ", option.Choices)}.");
            }
            result[id] = value;
        }

        return result;
    }

    private static string? Blocked(bool detected, bool hasModels, string name, string directory) =>
        !detected ? $"{name} is not installed for this user ({directory} was not found)."
        : !hasModels ? "The router has no enabled upstream model to offer yet."
        : null;

    private string ConfigPathFor(string agentId) => agentId switch
    {
        CodexRouterConnector.Id => _codex.ConfigPath,
        ClaudeCodeRouterConnector.Id => _claude.ConfigPath,
        _ => throw new KeyNotFoundException($"No connector for agent '{agentId}'.")
    };

    private static string DisplayName(string agentId) => agentId switch
    {
        CodexRouterConnector.Id => "Codex",
        ClaudeCodeRouterConnector.Id => "Claude Code",
        _ => agentId
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Validate(string? selector, IReadOnlyList<string> selectable, string what)
    {
        var trimmed = selector?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return selectable[0];
        if (!selectable.Contains(trimmed, StringComparer.Ordinal))
            throw new ArgumentException($"The {what} '{trimmed}' is not a selector this router serves.");
        return trimmed;
    }

    private static Dictionary<string, string?> Merge(RouterAgentState state, IReadOnlyDictionary<string, string?> discovered)
    {
        // A reconnect must not overwrite what was recorded the first time: that is the only record of
        // the owner's original values.
        var merged = new Dictionary<string, string?>(state.Previous, StringComparer.Ordinal);
        foreach (var (key, value) in discovered)
            if (!merged.ContainsKey(key) || merged[key] is null) merged[key] = value;
        return merged;
    }

    private void Backup(string path, RouterAgentState state, string reason)
    {
        if (!File.Exists(path)) return;
        var now = _clock.GetUtcNow();
        var name = $"{Path.GetFileName(path)}.{now:yyyyMMdd-HHmmss}.{Guid.NewGuid().ToString("N")[..6]}.bak";
        var directory = Path.Combine(_stateDir, "agent-backups");
        UnixFilePermissions.CreateOwnerOnlyDirectory(directory);
        var target = Path.Combine(directory, name);
        File.Copy(path, target, overwrite: true);
        UnixFilePermissions.RestrictFile(target);
        state.Backups.Add(new RouterAgentBackup(Path.GetFileNameWithoutExtension(name), target, now, reason));

        while (state.Backups.Count > MaxBackupsPerAgent)
        {
            var oldest = state.Backups.OrderBy(b => b.CreatedAt).First();
            state.Backups.Remove(oldest);
            try { File.Delete(oldest.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, RouterAgentState> LoadStates()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_statePath)) return new Dictionary<string, RouterAgentState>(StringComparer.Ordinal);
                var text = File.ReadAllText(_statePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, RouterAgentState>>(text, StateOptions);
                return parsed is null
                    ? new Dictionary<string, RouterAgentState>(StringComparer.Ordinal)
                    : new Dictionary<string, RouterAgentState>(parsed, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged state file must not stop the page from loading: what is actually connected
                // is read from the agents' own files, and this only adds history and restore points.
                return new Dictionary<string, RouterAgentState>(StringComparer.Ordinal);
            }
        }
    }

    private void SaveStates(Dictionary<string, RouterAgentState> states)
    {
        lock (_gate)
        {
            UnixFilePermissions.CreateOwnerOnlyDirectory(_stateDir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(states, StateOptions) + Environment.NewLine);
            UnixFilePermissions.RestrictFile(_statePath);
        }
    }
}
