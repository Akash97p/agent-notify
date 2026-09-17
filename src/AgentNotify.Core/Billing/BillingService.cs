using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Core.Billing;

/// <summary>
/// Stored provider keys shown as live balance or spend from each provider's official API.
/// Plaintext keys exist only transiently while making a request; snapshots and caches never hold them.
/// </summary>
public sealed class BillingService : IDisposable
{
    public const int MaxAccounts = 16;
    private const long MaxBodyBytes = 1024 * 1024;

    private const string DeepSeekUrl = "https://api.deepseek.com/user/balance";
    private const string MoonshotUrl = "https://api.moonshot.ai/v1/users/me/balance";
    private const string SiliconFlowUrl = "https://api.siliconflow.com/v1/user/info";
    private const string OpenRouterUrl = "https://openrouter.ai/api/v1/key";
    private const string OpenAICostsBase = "https://api.openai.com/v1/organization/costs";
    private const string AnthropicCostsBase = "https://api.anthropic.com/v1/organizations/cost_report";

    private readonly BillingAccountRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private bool _disposed;

    public BillingService(
        BillingAccountRepository repository,
        ISecretProtector protector,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _clock = clock ?? TimeProvider.System;
        if (handler is null)
        {
            var inner = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            _client = new HttpClient(inner) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
        else
        {
            _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        if (_ownsClient) _client.Dispose();
    }

    public static string NormalizeProvider(string? provider) =>
        BillingCatalog.IsSupported(provider)
            ? provider!
            : throw new ArgumentException("Choose a supported provider.");

    public static string NormalizeLabel(string? label)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 60 || trimmed.Any(char.IsControl))
            throw new ArgumentException("Give this account a name of 1–60 characters.");
        return trimmed;
    }

    public static void ValidateKey(string? key)
    {
        if (key is not { Length: >= 8 and <= 512 } || key.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Enter an API key of 8–512 printable characters without whitespace.");
    }

    public static string NewId() => "b_" + Guid.NewGuid().ToString("N");

    public async Task<IReadOnlyList<BillingAccount>> ListAsync(CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var stored = await _repository.ListStoredAsync(ct).ConfigureAwait(false);
        return stored.Select(ToPublic).ToList();
    }

    public async Task<BillingAccount> CreateAsync(string? provider, string? label, string? apiKey, CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedLabel = NormalizeLabel(label);
        ValidateKey(apiKey);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        if (await _repository.CountAsync(ct).ConfigureAwait(false) >= MaxAccounts)
            throw new ArgumentException("Up to 16 API accounts can be stored.");
        var now = _clock.GetUtcNow();
        var stored = new StoredBillingAccount(
            NewId(), normalizedProvider, normalizedLabel,
            _protector.Protect(apiKey!), now, now);
        await _repository.InsertAsync(stored, ct).ConfigureAwait(false);
        return ToPublic(stored);
    }

    public async Task<BillingAccount> UpdateAsync(string id, string? label, string? apiKey, CancellationToken ct = default)
    {
        var normalizedLabel = NormalizeLabel(label);
        var replaceKey = !string.IsNullOrEmpty(apiKey);
        if (replaceKey) ValidateKey(apiKey);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var existing = await _repository.GetStoredAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That account was not found.");
        var now = _clock.GetUtcNow();
        var encrypted = replaceKey ? _protector.Protect(apiKey!) : existing.EncryptedKey;
        var updated = existing with { Label = normalizedLabel, EncryptedKey = encrypted, UpdatedAt = now };
        await _repository.UpdateAsync(updated, ct).ConfigureAwait(false);
        lock (_cache)
        {
            if (replaceKey)
            {
                _cache.Remove(id);
                _retryAfter.Remove(id);
            }
            else if (_cache.TryGetValue(id, out var cached))
            {
                _cache[id] = cached with { Snapshot = cached.Snapshot with { Label = normalizedLabel } };
            }
        }
        return ToPublic(updated);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        if (!await _repository.DeleteAsync(id, ct).ConfigureAwait(false))
            throw new KeyNotFoundException("That account was not found.");
        lock (_cache)
        {
            _cache.Remove(id);
            _retryAfter.Remove(id);
        }
    }

    private static BillingAccount ToPublic(StoredBillingAccount stored) =>
        new(stored.Id, stored.Provider, stored.Label, stored.CreatedAt, stored.UpdatedAt);

    public async Task<BillingReport> GetReportAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var stored = await _repository.ListStoredAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<BillingSnapshot>(stored.Count);
            foreach (var account in stored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await SnapshotForAsync(account, refresh, cancellationToken).ConfigureAwait(false));
            }
            lock (_cache)
            {
                foreach (var id in _cache.Keys.Where(id => stored.All(a => a.Id != id)).ToArray())
                {
                    _cache.Remove(id);
                    _retryAfter.Remove(id);
                }
            }
            return new BillingReport(_clock.GetUtcNow(), results);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BillingSnapshot> SnapshotForAsync(
        StoredBillingAccount account, bool refresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        CacheEntry? cached;
        lock (_cache) _cache.TryGetValue(account.Id, out cached);
        if (cached is not null && !string.Equals(cached.Snapshot.Label, account.Label, StringComparison.Ordinal))
            cached = cached with { Snapshot = cached.Snapshot with { Label = account.Label } };
        DateTimeOffset? retryUntil = null;
        lock (_cache)
        {
            if (_retryAfter.TryGetValue(account.Id, out var retryValue))
                retryUntil = retryValue;
        }
        if (retryUntil.HasValue && now < retryUntil.Value && cached is not null)
            return cached.Snapshot;

        var due = cached is null
            || (refresh && now - cached.AttemptedAt >= TimeSpan.FromSeconds(60))
            || now - cached.AttemptedAt >= TimeSpan.FromMinutes(5);
        if (!due) return cached!.Snapshot;

        BillingSnapshot result;
        try
        {
            string key;
            try
            {
                key = _protector.Unprotect(account.EncryptedKey);
            }
            catch (Exception error) when (error is ArgumentException or System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Nothing was sent; the envelope belongs to another user or machine, or its key was lost.
                result = BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
                    "unavailable", "The stored key could not be decrypted. Replace the key for this account.");
                lock (_cache) _cache[account.Id] = new CacheEntry(now, result);
                return result;
            }

            result = await FetchAsync(account, key, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BillingParseException)
        {
            result = BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
                "unavailable", "The provider's response could not be read.");
        }
        catch
        {
            result = BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
                "unavailable", "The provider could not be reached.");
        }
        return CacheAndStale(account.Id, now, cached, result);
    }

    private BillingSnapshot CacheAndStale(string id, DateTimeOffset now, CacheEntry? cached, BillingSnapshot result)
    {
        if ((result.Status is "unavailable" or "rate_limited") &&
            cached?.Snapshot is { Status: "ok" } prior)
        {
            var stale = prior with { Message = "Showing the last successful check." };
            lock (_cache) _cache[id] = new CacheEntry(now, stale);
            return stale;
        }
        lock (_cache) _cache[id] = new CacheEntry(now, result);
        return result;
    }

    private Task<BillingSnapshot> FetchAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct) =>
        account.Provider switch
        {
            "deepseek" => FetchDeepSeekAsync(account, key, now, ct),
            "moonshot" => FetchMoonshotAsync(account, key, now, ct),
            "siliconflow" => FetchSiliconFlowAsync(account, key, now, ct),
            "openrouter" => FetchOpenRouterAsync(account, key, now, ct),
            "openai_admin" => FetchOpenAIAsync(account, key, now, ct),
            "anthropic_admin" => FetchAnthropicAsync(account, key, now, ct),
            _ => Task.FromResult(BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
                "unavailable", "The provider could not be reached.")),
        };

    private async Task<BillingSnapshot> FetchDeepSeekAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
        if (outcome.Status is not null) return ErrorSnapshot(account, outcome);
        using var root = outcome.Json!;
        if (root.RootElement.ValueKind != JsonValueKind.Object ||
            !root.RootElement.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array)
            throw new BillingParseException();
        var isAvailable = GetBoolOrNull(root.RootElement, "is_available");
        var balances = new List<BillingBalance>();
        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object)
                throw new BillingParseException();
            var currency = GetString(info, "currency");
            var total = GetString(info, "total_balance");
            if (string.IsNullOrWhiteSpace(currency) || total is null)
                throw new BillingParseException();
            balances.Add(new BillingBalance("total", currency, ParseDecimalString(total, allowNegative: true)));
            var granted = GetString(info, "granted_balance");
            if (granted is not null) balances.Add(new BillingBalance("granted", currency, ParseDecimalString(granted, allowNegative: true)));
            var topped = GetString(info, "topped_up_balance");
            if (topped is not null) balances.Add(new BillingBalance("topped_up", currency, ParseDecimalString(topped, allowNegative: true)));
        }
        return new BillingSnapshot(account.Id, account.Provider, account.Label, "ok", now, null,
            balances, [], [], isAvailable);
    }

    private async Task<BillingSnapshot> FetchMoonshotAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MoonshotUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
        if (outcome.Status is not null) return ErrorSnapshot(account, outcome);
        using var root = outcome.Json!;
        if (root.RootElement.ValueKind != JsonValueKind.Object ||
            !root.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            throw new BillingParseException();
        var balances = new List<BillingBalance>
        {
            // Moonshot documents a non-positive available balance, so balances may be negative.
            new("available", "USD", GetDecimal(data, "available_balance", allowNegative: true)),
            new("voucher", "USD", GetDecimal(data, "voucher_balance", allowNegative: true)),
            new("cash", "USD", GetDecimal(data, "cash_balance", allowNegative: true)),
        };
        return new BillingSnapshot(account.Id, account.Provider, account.Label, "ok", now, null,
            balances, [], [], null);
    }

    private async Task<BillingSnapshot> FetchSiliconFlowAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SiliconFlowUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
        if (outcome.Status is not null) return ErrorSnapshot(account, outcome);
        using var root = outcome.Json!;
        if (root.RootElement.ValueKind != JsonValueKind.Object ||
            !root.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            throw new BillingParseException();
        // Currency is undocumented: always null, never a symbol. Other data fields are ignored.
        var balances = new List<BillingBalance>
        {
            new("balance", null, GetDecimalOrString(data, "balance", allowNegative: true)),
            new("charge_balance", null, GetDecimalOrString(data, "chargeBalance", allowNegative: true)),
            new("total_balance", null, GetDecimalOrString(data, "totalBalance", allowNegative: true)),
        };
        return new BillingSnapshot(account.Id, account.Provider, account.Label, "ok", now, null,
            balances, [], [], null);
    }

    private async Task<BillingSnapshot> FetchOpenRouterAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenRouterUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json");
        var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
        if (outcome.Status is not null) return ErrorSnapshot(account, outcome);
        using var root = outcome.Json!;
        if (root.RootElement.ValueKind != JsonValueKind.Object ||
            !root.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            throw new BillingParseException();
        var spend = new List<BillingSpend>
        {
            new("today", "USD", GetDecimal(data, "usage_daily")),
            new("this_week", "USD", GetDecimal(data, "usage_weekly")),
            new("this_month", "USD", GetDecimal(data, "usage_monthly")),
            new("all_time", "USD", GetDecimal(data, "usage")),
        };
        var balances = new List<BillingBalance>();
        if (data.TryGetProperty("limit_remaining", out var remaining) && remaining.ValueKind != JsonValueKind.Null)
        {
            if (remaining.ValueKind != JsonValueKind.Number || !remaining.TryGetDecimal(out var value))
                throw new BillingParseException();
            balances.Add(new BillingBalance("limit_remaining", "USD", value));
        }
        return new BillingSnapshot(account.Id, account.Provider, account.Label, "ok", now, null,
            balances, spend, [], null);
    }

    private async Task<BillingSnapshot> FetchOpenAIAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        var startMidnight = now.UtcDateTime.Date.AddDays(-30);
        var startUnix = new DateTimeOffset(startMidnight, TimeSpan.Zero).ToUnixTimeSeconds();
        var endUnix = now.ToUnixTimeSeconds();
        var baseUrl = $"{OpenAICostsBase}?start_time={startUnix}&end_time={endUnix}&bucket_width=1d&limit=31";
        var daily = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        string? page = null;
        for (var i = 0; i < 5; i++)
        {
            var url = page is null ? baseUrl : baseUrl + "&page=" + Uri.EscapeDataString(page);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.ParseAdd("application/json");
            var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
            if (outcome.Status is not null) return ErrorSnapshot(account, outcome);
            using var root = outcome.Json!;
            if (root.RootElement.ValueKind != JsonValueKind.Object ||
                !root.RootElement.TryGetProperty("data", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
                throw new BillingParseException();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                if (!bucket.TryGetProperty("start_time", out var start) ||
                    start.ValueKind != JsonValueKind.Number || !start.TryGetInt64(out var seconds))
                    continue;
                string date;
                try { date = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
                catch (ArgumentOutOfRangeException) { continue; }
                decimal sum = 0;
                if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var result in results.EnumerateArray())
                    {
                        if (result.ValueKind != JsonValueKind.Object) continue;
                        if (!result.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Object)
                            continue;
                        if (!amount.TryGetProperty("value", out var value) ||
                            value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0)
                            continue;
                        sum += number;
                    }
                }
                daily[date] = (daily.TryGetValue(date, out var prior) ? prior : 0) + sum;
            }
            if (!TryNextPage(root.RootElement, out var next)) break;
            page = next;
        }
        return SpendSnapshot(account, now, daily);
    }

    private async Task<BillingSnapshot> FetchAnthropicAsync(
        StoredBillingAccount account, string key, DateTimeOffset now, CancellationToken ct)
    {
        var startMidnight = now.UtcDateTime.Date.AddDays(-30);
        var startIso = new DateTimeOffset(startMidnight, TimeSpan.Zero)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var endIso = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var baseUrl = $"{AnthropicCostsBase}?starting_at={Uri.EscapeDataString(startIso)}&ending_at={Uri.EscapeDataString(endIso)}";
        var daily = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        string? page = null;
        for (var i = 0; i < 5; i++)
        {
            var url = page is null ? baseUrl : baseUrl + "&page=" + Uri.EscapeDataString(page);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.Accept.ParseAdd("application/json");
            var outcome = await SendAsync(request, account.Id, now, ct).ConfigureAwait(false);
            if (outcome.Status is not null)
            {
                // Individual Anthropic accounts have no Admin API surface.
                if (outcome.HttpCode is 401 or 403 or 404)
                    return BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
                        outcome.Status, "Not available for this key/organization.");
                return ErrorSnapshot(account, outcome);
            }
            using var root = outcome.Json!;
            if (root.RootElement.ValueKind != JsonValueKind.Object ||
                !root.RootElement.TryGetProperty("data", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
                throw new BillingParseException();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                if (!bucket.TryGetProperty("starting_at", out var start) ||
                    start.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed))
                    continue;
                var date = parsed.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                decimal sum = 0;
                if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var result in results.EnumerateArray())
                    {
                        if (result.ValueKind != JsonValueKind.Object) continue;
                        if (!result.TryGetProperty("amount", out var amount) ||
                            amount.ValueKind != JsonValueKind.String)
                            continue;
                        // Official docs: decimal strings in lowest units (cents).
                        if (!decimal.TryParse(amount.GetString(), NumberStyles.Number,
                                CultureInfo.InvariantCulture, out var cents) || cents < 0)
                            continue;
                        sum += cents / 100m;
                    }
                }
                daily[date] = (daily.TryGetValue(date, out var prior) ? prior : 0) + sum;
            }
            if (!TryNextPage(root.RootElement, out var next)) break;
            page = next;
        }
        return SpendSnapshot(account, now, daily);
    }

    private static bool TryNextPage(JsonElement root, out string? next)
    {
        next = null;
        var hasMore = root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("has_more", out var more) &&
            more.ValueKind == JsonValueKind.True;
        if (!hasMore) return false;
        if (!root.TryGetProperty("next_page", out var nextElement) ||
            nextElement.ValueKind != JsonValueKind.String)
            return false;
        var value = nextElement.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Any(char.IsControl))
            return false;
        next = value;
        return true;
    }

    private static BillingSnapshot SpendSnapshot(
        StoredBillingAccount account, DateTimeOffset now, SortedDictionary<string, decimal> daily)
    {
        var month = now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        decimal monthTotal = 0;
        decimal total = 0;
        var days = new List<BillingDaily>(daily.Count);
        foreach (var (date, amount) in daily)
        {
            total += amount;
            if (date.StartsWith(month, StringComparison.Ordinal)) monthTotal += amount;
            days.Add(new BillingDaily(date, "USD", amount));
        }
        var spend = new List<BillingSpend>
        {
            new("last_30_days", "USD", total),
            new("month_to_date", "USD", monthTotal),
        };
        return new BillingSnapshot(account.Id, account.Provider, account.Label, "ok", now, null,
            [], spend, days, null);
    }

    private sealed record SendOutcome(
        string? Status,
        string Message,
        int? HttpCode,
        JsonDocument? Json);

    private static BillingSnapshot ErrorSnapshot(StoredBillingAccount account, SendOutcome outcome) =>
        BillingSnapshot.Unavailable(account.Id, account.Provider, account.Label,
            outcome.Status ?? "unavailable", outcome.Message);

    private async Task<SendOutcome> SendAsync(
        HttpRequestMessage request, string accountId, DateTimeOffset now, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendOutcome("unavailable", "The provider could not be reached.", null, null);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
        {
            return new SendOutcome("unavailable", "The provider could not be reached.", null, null);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SendOutcome("unauthorized", "The provider rejected this key.", (int)response.StatusCode, null);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new SendOutcome("unavailable", "This key cannot read that data.", 404, null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow())
                    ?? TimeSpan.FromSeconds(60);
                var seconds = Math.Clamp(delay.TotalSeconds, 1, 3600);
                lock (_cache) _retryAfter[accountId] = now + TimeSpan.FromSeconds(seconds);
                return new SendOutcome("rate_limited", "The provider is rate limiting requests. Try again later.", 429, null);
            }
            if (!response.IsSuccessStatusCode)
                return new SendOutcome("unavailable", $"The provider returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode, null);
            string text;
            try
            {
                if (response.Content.Headers.ContentLength > MaxBodyBytes)
                    return new SendOutcome("unavailable", "The provider's response could not be read.", null, null);
                await response.Content.LoadIntoBufferAsync(MaxBodyBytes, timeout.Token).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (Encoding.UTF8.GetByteCount(text) > MaxBodyBytes)
                    return new SendOutcome("unavailable", "The provider's response could not be read.", null, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SendOutcome("unavailable", "The provider could not be reached.", null, null);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return new SendOutcome("unavailable", "The provider could not be reached.", null, null);
            }
            try
            {
                var json = JsonDocument.Parse(text);
                return new SendOutcome(null, "", null, json);
            }
            catch (JsonException)
            {
                return new SendOutcome("unavailable", "The provider's response could not be read.", null, null);
            }
        }
    }

    private sealed record CacheEntry(DateTimeOffset AttemptedAt, BillingSnapshot Snapshot);

    private sealed class BillingParseException : Exception;

    private static string? GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var child))
            return null;
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }

    private static bool? GetBoolOrNull(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var child))
            return null;
        return child.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static decimal GetDecimal(JsonElement parent, string name, bool allowNegative = false)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var child) ||
            child.ValueKind != JsonValueKind.Number ||
            !child.TryGetDecimal(out var value) ||
            value < 0 && !allowNegative)
            throw new BillingParseException();
        return value;
    }

    private static decimal GetDecimalOrString(JsonElement parent, string name, bool allowNegative = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var child))
            throw new BillingParseException();
        if (child.ValueKind == JsonValueKind.Number &&
            child.TryGetDecimal(out var number) && (number >= 0 || allowNegative))
            return number;
        if (child.ValueKind == JsonValueKind.String &&
            decimal.TryParse(child.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            (parsed >= 0 || allowNegative))
            return parsed;
        throw new BillingParseException();
    }

    private static decimal ParseDecimalString(string? value, bool allowNegative = false)
    {
        if (value is null ||
            !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 && !allowNegative)
            throw new BillingParseException();
        return parsed;
    }
}
