using System.Security.Cryptography;

namespace AgentNotify.Api.Auth;

/// <summary>Rejects any request under /v1 without a valid local bearer token.
/// Uses constant-time comparison to avoid trivial timing leaks.</summary>
public static class TokenAuth
{
    public static bool IsAuthorized(string? authorizationHeader, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(authorizationHeader))
            return false;

        const string scheme = "Bearer ";
        var value = authorizationHeader.Trim();
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var token = value[scheme.Length..].Trim();
        return FixedTimeEquals(token, expectedToken);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a));
        var bHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }
}
