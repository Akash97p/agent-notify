using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentNotify.Core.Router;

/// <summary>What one upstream request authenticates with: a bearer credential and any extra headers.</summary>
public sealed record UpstreamCredential(string? Secret, IReadOnlyDictionary<string, string> Headers)
{
    public static readonly UpstreamCredential None = new(null, new Dictionary<string, string>());
}

/// <summary>
/// A subscription sign-in could not be used: the other tool is not signed in, or its sign-in has
/// expired and could not be renewed. The message says what to run, and never carries a token.
/// </summary>
public sealed class SubscriptionAuthException(string message) : Exception(message);

/// <summary>
/// Supplies the credential for each upstream attempt. An API-key upstream decrypts its stored key;
/// a subscription upstream reuses the sign-in another tool keeps on this computer, so AgentNotify
/// stores nothing for it.
/// </summary>
/// <remarks>
/// Both subscription kinds are unofficial: neither plan documents third-party use. They are offered
/// because the owner asked for them, are opt-in per upstream, and are labelled so in the interface.
/// Tokens are held in memory only as long as a request needs them and never reach a log or the
/// ledger.
/// </remarks>
public sealed class RouterCredentialSource
{
    // Codex's own public OAuth client and token endpoint, used exactly as Codex uses them to renew
    // its sign-in. Renewing here writes the new tokens back to Codex's file, so Codex keeps working.
    internal const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal const string CodexTokenUrl = "https://auth.openai.com/oauth/token";
    internal const string MuseKeyUrl = "https://api.meta.ai/muse-code/key";

    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MuseKeyLifetime = TimeSpan.FromHours(23);

    private readonly RouterConfigService _config;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, SemaphoreSlim> _codexLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _museLock = new(1, 1);
    private readonly Dictionary<string, (string Key, DateTimeOffset Expires, string Identity)> _museKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a key kept under Live quota's API accounts, by account ID; null when that account is gone.
    /// Set by the host, which owns that list; without it an <c>api_account:</c> reference cannot be used.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? ApiAccountKey { get; set; }

    public RouterCredentialSource(RouterConfigService config, HttpClient http, TimeProvider? clock = null,
        string? codexHome = null, string? museHome = null, string? openCodeAuthPath = null)
    {
        _config = config;
        _http = http;
        _clock = clock ?? TimeProvider.System;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CodexHome = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        MuseHome = museHome ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config"),
            "muse");
        OpenCodeAuthPath = openCodeAuthPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share"),
            "opencode", "auth.json");
    }

    /// <summary>Where OpenCode keeps the API keys it was given, one per provider ID.</summary>
    public string OpenCodeAuthPath { get; }

    /// <summary>
    /// The API key OpenCode already holds for a provider, so adding that provider here needs no
    /// copy and paste. Only plain API keys are returned; OpenCode's OAuth sign-ins are not reused.
    /// </summary>
    public string? ReadOpenCodeKey(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        try
        {
            if (!File.Exists(OpenCodeAuthPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(OpenCodeAuthPath)) as JsonObject;
            if (root?[providerId] is not JsonObject entry || (string?)entry["type"] != "api") return null;
            var key = (string?)entry["key"];
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public string CodexHome { get; }
    public string MuseHome { get; }
    public string CodexAuthPath => Path.Combine(CodexHome, "auth.json");
    public string MuseAuthPath => Path.Combine(MuseHome, "auth.json");

    /// <summary>
    /// The credential for one attempt. <paramref name="renew"/> forces a subscription sign-in to be
    /// renewed, after the upstream rejected the current one.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A stored key cannot be opened here.</exception>
    /// <exception cref="SubscriptionAuthException">A subscription sign-in is missing or cannot be renewed.</exception>
    public async Task<UpstreamCredential> GetAsync(StoredRouterUpstream upstream, bool renew, CancellationToken ct)
    {
        var profile = RouterCredentialRef.ProfileDirectory(upstream.CredentialRef);
        switch (upstream.Auth)
        {
            case RouterAuth.CodexChatGpt:
                return await CodexAsync(profile ?? CodexHome, renew, ct).ConfigureAwait(false);
            case RouterAuth.MuseCode:
                return new UpstreamCredential(await MuseKeyAsync(profile ?? MuseHome, renew, ct).ConfigureAwait(false),
                    new Dictionary<string, string>());
            default:
                var key = RouterCredentialRef.ApiAccountId(upstream.CredentialRef) is { } accountId
                    ? await ApiAccountKeyAsync(accountId, ct).ConfigureAwait(false)
                    : _config.DecryptKey(upstream);
                return key is null ? UpstreamCredential.None : new UpstreamCredential(key, new Dictionary<string, string>());
        }
    }

    /// <summary>The key of one API account; a missing account fails the attempt rather than sending none.</summary>
    public async Task<string> ApiAccountKeyAsync(string accountId, CancellationToken ct)
    {
        if (ApiAccountKey is null)
            throw new SubscriptionAuthException("API accounts are not available in this host.");
        return await ApiAccountKey(accountId, ct).ConfigureAwait(false)
            ?? throw new SubscriptionAuthException("The API account this provider uses was removed. Choose another key.");
    }

    /// <summary>
    /// The credential for listing a provider's models before any upstream is saved: the key the
    /// owner just typed, or the subscription sign-in.
    /// </summary>
    public async Task<UpstreamCredential> GetForAuthAsync(string auth, string? plainKey, CancellationToken ct,
        string? profileDirectory = null) => auth switch
    {
        RouterAuth.CodexChatGpt => await CodexAsync(profileDirectory ?? CodexHome, false, ct).ConfigureAwait(false),
        RouterAuth.MuseCode => new UpstreamCredential(await MuseKeyAsync(profileDirectory ?? MuseHome, false, ct).ConfigureAwait(false), new Dictionary<string, string>()),
        _ => string.IsNullOrEmpty(plainKey) ? UpstreamCredential.None : new UpstreamCredential(plainKey, new Dictionary<string, string>())
    };

    /// <summary>Whether this computer has the sign-in a subscription kind needs, for the interface.</summary>
    public (bool Ready, string Detail) Describe(string auth, string? profileDirectory = null) => auth switch
    {
        RouterAuth.CodexChatGpt => ReadCodexAuth(profileDirectory ?? CodexHome) is { } codex
            ? (true, codex.AccountId is null ? "Signed in to Codex." : "Signed in to Codex with a ChatGPT account.")
            : (false, "Codex is not signed in with a ChatGPT account on this computer. Run 'codex login' first."),
        RouterAuth.MuseCode => ReadMuseIdentity(profileDirectory ?? MuseHome) is not null
            ? (true, "Signed in to Muse Code.")
            : (false, $"No Muse Code sign-in was found at {Path.Combine(profileDirectory ?? MuseHome, "auth.json")}. Run 'muse-code auth login' first."),
        _ => (true, "")
    };

    // ---- ChatGPT plan through Codex's sign-in --------------------------------------------------

    internal sealed record CodexAuth(string AccessToken, string? RefreshToken, string? AccountId, DateTimeOffset? Expires);

    private async Task<UpstreamCredential> CodexAsync(string home, bool renew, CancellationToken ct)
    {
        var auth = ReadCodexAuth(home)
            ?? throw new SubscriptionAuthException($"The Codex account at {home} is not signed in with a ChatGPT account. Run 'codex login'.");
        if (renew || auth.Expires is { } expires && expires - RenewAhead <= _clock.GetUtcNow())
        {
            SemaphoreSlim gate;
            lock (_codexLocks)
            {
                if (!_codexLocks.TryGetValue(home, out gate!)) _codexLocks[home] = gate = new SemaphoreSlim(1, 1);
            }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Codex itself may have renewed while this request waited; use that instead of
                // spending the refresh token a second time.
                var current = ReadCodexAuth(home) ?? auth;
                auth = current.AccessToken != auth.AccessToken && !IsExpiring(current)
                    ? current
                    : await RenewCodexAsync(home, current, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        var headers = new Dictionary<string, string>
        {
            ["OpenAI-Beta"] = "responses=experimental",
            ["originator"] = "codex_cli_rs"
        };
        if (auth.AccountId is not null) headers["chatgpt-account-id"] = auth.AccountId;
        return new UpstreamCredential(auth.AccessToken, headers);
    }

    private bool IsExpiring(CodexAuth auth) =>
        auth.Expires is { } expires && expires - RenewAhead <= _clock.GetUtcNow();

    internal CodexAuth? ReadCodexAuth(string? home = null)
    {
        var path = Path.Combine(home ?? CodexHome, "auth.json");
        try
        {
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["tokens"] is not JsonObject tokens) return null;
            var access = (string?)tokens["access_token"];
            if (string.IsNullOrWhiteSpace(access)) return null;
            return new CodexAuth(access, (string?)tokens["refresh_token"], (string?)tokens["account_id"], JwtExpiry(access));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<CodexAuth> RenewCodexAsync(string home, CodexAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(auth.RefreshToken))
            throw new SubscriptionAuthException("The Codex sign-in has expired. Run 'codex login'.");

        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_id"] = CodexClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = auth.RefreshToken!,
            ["scope"] = "openid profile email"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexTokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        JsonObject? result;
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SubscriptionAuthException(
                    $"Renewing the Codex sign-in failed (HTTP {(int)response.StatusCode}). Run 'codex login'.");
            result = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) as JsonObject;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SubscriptionAuthException("Renewing the Codex sign-in failed. Run 'codex login'.");
        }

        var access = (string?)result?["access_token"];
        if (string.IsNullOrWhiteSpace(access))
            throw new SubscriptionAuthException("Renewing the Codex sign-in returned no token. Run 'codex login'.");

        // Write the renewed tokens back the way Codex stores them, so Codex and the router go on
        // sharing one sign-in instead of invalidating each other's refresh token.
        WriteCodexTokens(Path.Combine(home, "auth.json"), access, (string?)result!["refresh_token"], (string?)result["id_token"]);
        return new CodexAuth(access, (string?)result["refresh_token"] ?? auth.RefreshToken, auth.AccountId, JwtExpiry(access));
    }

    private void WriteCodexTokens(string authPath, string access, string? refresh, string? idToken)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(authPath)) as JsonObject ?? new JsonObject();
            var tokens = root["tokens"] as JsonObject ?? new JsonObject();
            tokens["access_token"] = access;
            if (!string.IsNullOrWhiteSpace(refresh)) tokens["refresh_token"] = refresh;
            if (!string.IsNullOrWhiteSpace(idToken)) tokens["id_token"] = idToken;
            root["tokens"] = tokens;
            root["last_refresh"] = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
            var temp = authPath + ".agentnotify.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            UnixFilePermissions.RestrictFile(temp);
            File.Move(temp, authPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // The renewed token still serves this request; Codex renews again on its own next start.
        }
    }

    /// <summary>The <c>exp</c> claim of a JWT, without verifying it: only used to decide when to renew.</summary>
    internal static DateTimeOffset? JwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    // ---- Muse Code plan through Muse Code's sign-in --------------------------------------------

    /// <summary>
    /// Muse Code's identity token. Its CLI may keep the sign-in in the OS keychain instead of this
    /// file; only the file is read.
    /// </summary>
    internal string? ReadMuseIdentity(string? home = null)
    {
        var path = Path.Combine(home ?? MuseHome, "auth.json");
        try
        {
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var token = (string?)root?["access_token"] ?? (string?)root?["accessToken"];
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A subscription Model API key, minted from the Muse Code sign-in the way Muse Code's own CLI
    /// does, and kept in memory only.
    /// </summary>
    private async Task<string> MuseKeyAsync(string home, bool renew, CancellationToken ct)
    {
        var identity = ReadMuseIdentity(home)
            ?? throw new SubscriptionAuthException("Muse Code is not signed in on this computer. Run 'muse-code auth login'.");
        await _museLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!renew && _museKeys.TryGetValue(home, out var cached) && cached.Identity == identity && cached.Expires > _clock.GetUtcNow())
                return cached.Key;

            using var request = new HttpRequestMessage(HttpMethod.Post, MuseKeyUrl)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("x-api-version", "1.0.0");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string? key;
            try
            {
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new SubscriptionAuthException(
                        $"Muse Code refused to issue a key (HTTP {(int)response.StatusCode}). Run 'muse-code auth login'.");
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                key = (string?)json?["api_key"];
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new SubscriptionAuthException("Muse Code could not be reached to issue a key.");
            }
            if (string.IsNullOrWhiteSpace(key))
                throw new SubscriptionAuthException("Muse Code returned no key. Run 'muse-code auth login'.");
            _museKeys[home] = (key, _clock.GetUtcNow() + MuseKeyLifetime, identity);
            return key;
        }
        finally { _museLock.Release(); }
    }
}
