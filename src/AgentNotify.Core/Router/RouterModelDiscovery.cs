using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentNotify.Core.Router;

/// <summary>
/// Lists the models a provider offers, so adding one is "paste the key, tick the models" rather
/// than typing model IDs from its documentation.
/// </summary>
/// <remarks>
/// The request goes only to the base URL the owner is about to save, under the same destination
/// rule as routed traffic, and carries the key only there. The ChatGPT plan has no public model
/// list; Codex keeps the one its backend sent, and that file is read instead.
/// </remarks>
public sealed class RouterModelDiscovery
{
    public const int MaxModels = 500;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly RouterCredentialSource _credentials;

    public RouterModelDiscovery(HttpClient http, RouterCredentialSource credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    /// <exception cref="ArgumentException">The base URL fails the destination rule.</exception>
    /// <exception cref="SubscriptionAuthException">A subscription sign-in is missing.</exception>
    /// <exception cref="InvalidOperationException">The provider could not be asked, with a reason fit to show.</exception>
    public async Task<IReadOnlyList<string>> FetchAsync(string? baseUrl, string wire, string auth, string? key,
        RouterPreset? preset, CancellationToken ct, string? profileDirectory = null)
    {
        IEnumerable<string> models;
        if (auth == RouterAuth.CodexChatGpt)
        {
            models = ReadCodexModels(profileDirectory);
        }
        else
        {
            if (!RouterDestination.TryValidateBaseUrl(baseUrl, out _, out var normalized, out var error))
                throw new ArgumentException(error ?? "Enter a valid base URL.");
            var credential = await _credentials.GetForAuthAsync(auth, key, ct, profileDirectory).ConfigureAwait(false);
            models = await ListAsync(normalized!, wire, credential, ct).ConfigureAwait(false);
        }

        return models
            .Where(model => model.Length is > 0 and <= 200 && model.All(c => c is >= '!' and <= '~'))
            .Where(model => preset?.Supports(model) ?? true)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxModels)
            .ToList();
    }

    private async Task<List<string>> ListAsync(string baseUrl, string wire, UpstreamCredential credential, CancellationToken ct)
    {
        var url = baseUrl + "/models" + (wire == RouterWire.AnthropicMessages ? "?limit=1000" : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (credential.Secret is { } secret)
        {
            if (wire == RouterWire.AnthropicMessages)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", secret);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
        }
        foreach (var (name, value) in credential.Headers) request.Headers.TryAddWithoutValidation(name, value);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                throw new InvalidOperationException("The provider refused the key. Check it and try again.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The provider's model list returned HTTP {status}.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidOperationException("The provider's model list is too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length > MaxResponseBytes)
                throw new InvalidOperationException("The provider's model list is too large.");
            return ParseModelList(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The provider did not answer within 20 seconds.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The provider could not be reached.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The provider's model list was not in a format the router understands.");
        }
    }

    /// <summary>
    /// The OpenAI and Anthropic shape (<c>{"data":[{"id":…}]}</c>), and a bare array or
    /// <c>{"models":[…]}</c> that some local servers return.
    /// </summary>
    internal static List<string> ParseModelList(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : root.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array ? list
            : throw new JsonException("No model array.");
        var models = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(id)) models.Add(id!);
        }
        return models;
    }

    /// <summary>The models Codex's backend last offered this ChatGPT account, from Codex's own cache.</summary>
    internal IEnumerable<string> ReadCodexModels(string? home = null)
    {
        // A second account Codex has not been started from has no cache of its own yet; the default
        // account's is the same ChatGPT catalogue.
        var path = Path.Combine(home ?? _credentials.CodexHome, "models_cache.json");
        if (!File.Exists(path)) path = Path.Combine(_credentials.CodexHome, "models_cache.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Codex's model list is empty. Start Codex once, then try again.");
            var result = new List<string>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.TryGetProperty("visibility", out var visibility) && visibility.GetString() != "list") continue;
                if (model.TryGetProperty("slug", out var slug) && slug.GetString() is { Length: > 0 } id) result.Add(id);
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("Codex's model list was not found. Sign in to Codex and start it once, then try again.");
        }
    }
}
