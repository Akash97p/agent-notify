using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentNotify.Insights.Quota;

/// <summary>Read-only first-party Claude account usage query; never refreshes or stores OAuth tokens.</summary>
public sealed class ClaudeQuotaProbe : ILiveQuotaProbe
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly string _credentialPath;
    private readonly HttpClient _client;
    private DateTimeOffset _retryAfter;
    private string? _retryScope;

    public ClaudeQuotaProbe(string? credentialPath = null, HttpClient? client = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        _credentialPath = credentialPath ?? Path.Combine(configured ?? Path.Combine(home, ".claude"), ".credentials.json");
        _client = client ?? SharedClient;
    }

    public string Provider => "claude_code";
    public string ScopeKey() => QuotaFileScope.Of(_credentialPath);

    public async Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = ScopeKey();
        if (_retryScope != scope) { _retryScope = scope; _retryAfter = DateTimeOffset.MinValue; }
        if (now < _retryAfter)
            return LiveQuotaSnapshot.Unavailable(Provider, "Claude asked us to wait before checking quota again.", now);

        string? token;
        try
        {
            var file = new FileInfo(_credentialPath);
            if (!file.Exists || file.Length is < 2 or > 64 * 1024)
                return LiveQuotaSnapshot.Unavailable(Provider, "Sign in to Claude Code to see account quota.", now, "auth_required");
            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(_credentialPath, cancellationToken));
            var oauth = QuotaJson.Child(credentials.RootElement, "claudeAiOauth");
            token = QuotaJson.String(oauth, "accessToken");
            if (string.IsNullOrWhiteSpace(token) || token.Length > 8192)
                return LiveQuotaSnapshot.Unavailable(Provider, "Claude Code account credentials are unavailable.", now, "auth_required");
            var expiry = QuotaJson.Child(oauth, "expiresAt");
            if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var millis) &&
                DateTimeOffset.FromUnixTimeMilliseconds(millis) <= now)
                return LiveQuotaSnapshot.Unavailable(Provider, "Claude Code needs to refresh its sign-in before quota can be checked.", now, "auth_required");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        { return LiveQuotaSnapshot.Unavailable(Provider, "Claude Code account credentials could not be read.", now); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-code/2.1 AgentNotify/0.1");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromMinutes(5);
                _retryAfter = now + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 30, 3600));
                return LiveQuotaSnapshot.Unavailable(Provider, "Claude quota is rate limited; try again later.", now, "rate_limited");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return LiveQuotaSnapshot.Unavailable(Provider, "Claude Code sign-in cannot access quota right now.", now, "auth_required");
            if (!response.IsSuccessStatusCode)
                return LiveQuotaSnapshot.Unavailable(Provider, "Claude quota service is unavailable.", now);
            await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return Parse(json.RootElement, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return LiveQuotaSnapshot.Unavailable(Provider, "Claude quota probe timed out.", now); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException)
        { return LiveQuotaSnapshot.Unavailable(Provider, "Claude quota service could not be queried.", now); }
    }

    internal static LiveQuotaSnapshot Parse(JsonElement root, DateTimeOffset now)
    {
        var windows = new List<LiveQuotaWindow>();
        AddWindow(root, "five_hour", "5-hour", windows);
        AddWindow(root, "seven_day", "7-day", windows);
        AddWindow(root, "seven_day_opus", "Opus · 7-day", windows);
        AddWindow(root, "seven_day_sonnet", "Sonnet · 7-day", windows);
        var limits = QuotaJson.Child(root, "limits");
        if (limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray().Take(20))
            {
                var name = QuotaJson.String(limit, "name") ?? QuotaJson.String(limit, "type");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || windows.Any(window => window.Key == name)) continue;
                AddWindow(limit, name, name, windows, direct: true);
            }
        }
        return windows.Count == 0
            ? LiveQuotaSnapshot.Unavailable("claude_code", "Claude returned no account quota windows.", now)
            : new LiveQuotaSnapshot("claude_code", "ok", "Claude account usage", now, null, null, windows, null);
    }

    private static void AddWindow(JsonElement root, string key, string label, List<LiveQuotaWindow> windows, bool direct = false)
    {
        var value = direct ? root : QuotaJson.Child(root, key);
        var used = QuotaJson.Percent(value, "utilization");
        if (used is null) return;
        DateTimeOffset? reset = null;
        var raw = QuotaJson.String(value, "resets_at");
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            reset = parsed;
        windows.Add(new LiveQuotaWindow(key, label, used.Value, 100 - used.Value,
            key == "five_hour" ? 300 : key.StartsWith("seven_day", StringComparison.Ordinal) ? 10080 : null, reset));
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
