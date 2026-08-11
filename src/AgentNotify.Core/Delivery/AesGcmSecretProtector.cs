using System.Security.Cryptography;
using System.Text;

namespace AgentNotify.Core.Delivery;

/// <summary>Portable injected-key protector for tests and future non-Windows keychain adapters.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "aes-gcm:v1:";
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("A 256-bit key is required.", nameof(key));
        _key = key.ToArray();
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, bytes, cipher, tag);
            return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
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

        var payload = Convert.FromBase64String(envelope[Prefix.Length..]);
        if (payload.Length < 28)
            throw new CryptographicException("Invalid secret envelope.");

        var plaintext = new byte[payload.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plaintext);
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
