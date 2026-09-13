using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AgentNotify.Core.Quota;

/// <summary>Uses Codex's documented app-server RPC; AgentNotify never handles Codex tokens.</summary>
public sealed class CodexQuotaProbe : ILiveQuotaProbe
{
    private readonly string _executable;
    private readonly string _authPath;

    public CodexQuotaProbe(string? executable = null, string? codexHome = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        _authPath = Path.Combine(root, "auth.json");
        _executable = executable ?? ResolveExecutable(home);
    }

    public string Provider => "codex";
    public string ScopeKey() => QuotaFileScope.Of(_authPath);

    public async Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--stdio");
        // npm's Codex launcher uses `#!/usr/bin/env node`. Launchd often has a minimal PATH,
        // so include the launcher's directory where its paired Node binary is installed.
        if (Path.IsPathFullyQualified(_executable))
        {
            var directory = Path.GetDirectoryName(_executable)!;
            process.StartInfo.Environment.TryGetValue("PATH", out var inheritedPath);
            process.StartInfo.Environment["PATH"] = directory + Path.PathSeparator + inheritedPath;
        }
        var started = false;
        try
        {
            process.Start();
            started = true;
            // Drain but never expose stderr. The CLI owns its auth files and any diagnostics.
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            await SendAsync(process, new { method = "initialize", id = 1, @params = new
            {
                clientInfo = new { name = "agentnotify", title = "AgentNotify", version = "0.1.0" }
            } });
            await SendAsync(process, new { method = "initialized", @params = new { } });
            await SendAsync(process, new { method = "account/read", id = 2, @params = new { refreshToken = false } });
            await SendAsync(process, new { method = "account/rateLimits/read", id = 3 });

            string? plan = null;
            string? accountType = null;
            for (var lineCount = 0; lineCount < 80; lineCount++)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null || line.Length > 128 * 1024) break;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var id = QuotaJson.Int(root, "id");
                if (id == 2)
                {
                    var account = QuotaJson.Child(QuotaJson.Child(root, "result"), "account");
                    accountType = QuotaJson.String(account, "type");
                    plan = QuotaJson.String(account, "planType");
                }
                else if (id == 3)
                {
                    if (QuotaJson.Child(root, "error").ValueKind == JsonValueKind.Object)
                        return LiveQuotaSnapshot.Unavailable(Provider, "Codex rate limits are unavailable for this account.", now);
                    var result = QuotaJson.Child(root, "result");
                    if (result.ValueKind != JsonValueKind.Object || accountType == "apiKey")
                        return LiveQuotaSnapshot.Unavailable(Provider, "Sign in to Codex with a ChatGPT account to see quota.", now, "auth_required");
                    return Parse(result, now, plan);
                }
            }
            return LiveQuotaSnapshot.Unavailable(Provider, "Codex did not return a quota snapshot.", now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return LiveQuotaSnapshot.Unavailable(Provider, "Codex quota probe timed out.", now); }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException or InvalidOperationException)
        { return LiveQuotaSnapshot.Unavailable(Provider, "Codex app server could not be queried.", now); }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
            }
        }
    }

    internal static LiveQuotaSnapshot Parse(JsonElement result, DateTimeOffset now, string? accountPlan = null)
    {
        var buckets = QuotaJson.Child(result, "rateLimitsByLimitId");
        var windows = new List<LiveQuotaWindow>();
        string? plan = accountPlan;
        decimal? credits = null;
        if (buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in buckets.EnumerateObject().Take(20))
                AddBucket(entry.Value, entry.Name, windows, ref plan, ref credits);
        }
        if (windows.Count == 0)
            AddBucket(QuotaJson.Child(result, "rateLimits"), "codex", windows, ref plan, ref credits);
        return windows.Count == 0 && credits is null
            ? LiveQuotaSnapshot.Unavailable("codex", "Codex returned no active quota windows.", now)
            : new LiveQuotaSnapshot("codex", "ok", "Codex app server", now, plan, credits, windows, null);
    }

    private static void AddBucket(JsonElement bucket, string id, List<LiveQuotaWindow> windows,
        ref string? plan, ref decimal? credits)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        plan ??= QuotaJson.String(bucket, "planType");
        var credit = QuotaJson.Child(bucket, "credits");
        if (QuotaJson.Bool(credit, "hasCredits") == true && QuotaJson.Bool(credit, "unlimited") != true)
            credits ??= QuotaJson.Decimal(credit, "balance");
        var name = QuotaJson.String(bucket, "limitName") ?? (id == "codex" ? "Codex" : id);
        AddWindow(QuotaJson.Child(bucket, "primary"), id + ":primary", name, windows);
        AddWindow(QuotaJson.Child(bucket, "secondary"), id + ":secondary", name, windows);
    }

    private static void AddWindow(JsonElement value, string key, string bucketName, List<LiveQuotaWindow> windows)
    {
        var used = QuotaJson.Percent(value, "usedPercent");
        if (used is null) return;
        var duration = QuotaJson.Int(value, "windowDurationMins");
        var label = duration switch
        {
            300 => "5-hour",
            10080 => "7-day",
            > 0 and < 60 => duration + "-minute",
            > 0 and < 1440 => duration / 60d + "-hour",
            > 0 => duration / 1440d + "-day",
            _ => key.EndsWith("primary", StringComparison.Ordinal) ? "Primary" : "Secondary"
        };
        windows.Add(new LiveQuotaWindow(key, bucketName + " · " + label, used.Value, 100 - used.Value,
            duration, QuotaJson.UnixTime(value, "resetsAt")));
    }

    private static Task SendAsync(Process process, object value) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value));

    private static string ResolveExecutable(string home)
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        foreach (var candidate in new[] { Path.Combine(home, ".local", "node", "bin", "codex"),
                     Path.Combine(home, ".npm-global", "bin", "codex"), "/opt/homebrew/bin/codex", "/usr/local/bin/codex" })
            if (File.Exists(candidate)) return candidate;
        return "codex";
    }
}

internal static class QuotaJson
{
    internal static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;
    internal static string? String(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    internal static int? Int(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }
    internal static bool? Bool(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
    internal static decimal? Decimal(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0 ? number : null;
    }
    internal static double? Percent(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number) && number is >= 0 and <= 100 ? number : null;
    }
    internal static DateTimeOffset? UnixTime(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
