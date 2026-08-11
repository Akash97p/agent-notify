using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace AgentNotify.Core.Delivery;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi-user:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentNotify/provider-secrets/v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(string envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported secret envelope version.");

        var protectedBytes = Convert.FromBase64String(envelope[Prefix.Length..]);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
