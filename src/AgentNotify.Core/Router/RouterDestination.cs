namespace AgentNotify.Core.Router;

/// <summary>
/// Where a stored router key is allowed to travel. A validated base URL is the only destination the
/// proxy will attach an upstream credential to, so the rule is deliberately narrow: TLS everywhere
/// except a model server on this computer, and no part of the URL that could redirect the request
/// elsewhere.
/// </summary>
public static class RouterDestination
{
    public const int MaxLength = 512;

    /// <summary>
    /// Validates an upstream base URL and returns it normalized (no trailing slash), which is the
    /// exact prefix the wire path is appended to.
    /// </summary>
    public static bool TryValidateBaseUrl(string? value, out Uri? uri, out string? error)
        => TryValidateBaseUrl(value, out uri, out _, out error);

    public static bool TryValidateBaseUrl(string? value, out Uri? uri, out string? normalized, out string? error)
    {
        uri = null;
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Enter the provider's base URL.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            error = $"The base URL must be {MaxLength} characters or fewer.";
            return false;
        }

        trimmed = trimmed.TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "The base URL must be an absolute URL, such as https://api.example.com/v1.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "The base URL must not contain a user name or password.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "The base URL must not contain a query string or fragment.";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            // Only the exact loopback literals. Uri keeps an IPv6 literal in its brackets ("[::1]"),
            // so they are stripped before comparing. A name that merely resolves to a loopback
            // address is not accepted: DNS is not under the owner's control at request time.
            var host = parsed.Host.ToLowerInvariant().Trim('[', ']');
            if (host is not ("127.0.0.1" or "::1" or "localhost"))
            {
                error = "Plain http is only allowed for a model server on this computer (127.0.0.1, ::1, or localhost).";
                return false;
            }
        }
        else if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "The base URL must use https, or http for a server on this computer.";
            return false;
        }

        uri = parsed;
        normalized = trimmed;
        return true;
    }
}
