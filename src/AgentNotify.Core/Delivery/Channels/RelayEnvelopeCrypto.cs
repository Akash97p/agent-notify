using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using Org.BouncyCastle.Crypto.Parameters;

namespace AgentNotify.Core.Delivery.Channels;

/// <summary>
/// Envelope v1 sealing: X25519 key agreement with XChaCha20-Poly1305.
/// </summary>
/// <remarks>
/// <para>
/// Wire format is <c>base64url(nonce[24] || ephemeralPublicKey[32] || ciphertext||tag)</c>,
/// matching <c>docs/ENVELOPE.md</c> in the relay repository. The relay stores these bytes
/// and cannot read them; only the recipient device holds the private key.
/// </para>
/// <para>
/// The associated data binds version, sender, recipient, key id, event id and expiry, so a
/// tampered or replayed envelope fails authentication rather than decrypting into something
/// plausible. Every one of those fields is also sent in the clear as routing metadata, and
/// the two must agree exactly or the phone cannot decrypt.
/// </para>
/// <para>
/// BouncyCastle supplies X25519 and the IETF ChaCha20-Poly1305 AEAD. It does not expose
/// XChaCha20, whose 24-byte nonce is what lets the nonce be random rather than counted, so
/// the standard HChaCha20 subkey derivation is implemented here. It is pure managed code:
/// no native binary per runtime identifier to carry through the single-file installer.
/// </para>
/// </remarks>
internal static class RelayEnvelopeCrypto
{
    internal const int NonceLength = 24;
    internal const int KeyLength = 32;
    internal const int TagLength = 16;

    /// <summary>Minimum length of a well-formed envelope: nonce, ephemeral key, and a bare tag.</summary>
    internal const int MinimumWireLength = NonceLength + KeyLength + TagLength;

    /// <summary>
    /// Seals a payload for one device, generating a fresh ephemeral key pair and nonce.
    /// </summary>
    /// <exception cref="ArgumentException">The recipient key is not a 32-byte X25519 public key.</exception>
    internal static string Seal(
        string plaintext,
        string recipientPublicKeyBase64Url,
        string senderInstallationId,
        string deviceId,
        string keyId,
        string clientEventId,
        string expiresAt,
        string version = "1")
    {
        var recipientPublicKey = DecodeBase64Url(recipientPublicKeyBase64Url);
        if (recipientPublicKey.Length != KeyLength)
            throw new ArgumentException(
                $"Device public key must be {KeyLength} bytes, got {recipientPublicKey.Length}.",
                nameof(recipientPublicKeyBase64Url));

        // A new ephemeral pair per envelope: the sender's long-term identity never
        // appears in the ciphertext, and compromising one envelope's key reveals nothing
        // about any other.
        var ephemeralPrivateKey = RandomNumberGenerator.GetBytes(KeyLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        return Seal(
            Encoding.UTF8.GetBytes(plaintext),
            recipientPublicKey,
            ephemeralPrivateKey,
            nonce,
            BuildAssociatedData(version, senderInstallationId, deviceId, keyId, clientEventId, expiresAt));
    }

    /// <summary>
    /// Deterministic core, with the ephemeral key and nonce supplied.
    /// </summary>
    /// <remarks>
    /// Exposed so the cross-language test vectors can be reproduced byte for byte. Callers
    /// outside tests must use the overload that generates both, because reusing a nonce
    /// under the same key destroys the AEAD's security.
    /// </remarks>
    internal static string Seal(
        byte[] plaintext,
        byte[] recipientPublicKey,
        byte[] ephemeralPrivateKey,
        byte[] nonce,
        byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (recipientPublicKey.Length != KeyLength)
            throw new ArgumentException($"Recipient public key must be {KeyLength} bytes.", nameof(recipientPublicKey));
        if (ephemeralPrivateKey.Length != KeyLength)
            throw new ArgumentException($"Ephemeral private key must be {KeyLength} bytes.", nameof(ephemeralPrivateKey));
        if (nonce.Length != NonceLength)
            throw new ArgumentException($"Nonce must be {NonceLength} bytes.", nameof(nonce));

        var privateKey = new X25519PrivateKeyParameters(ephemeralPrivateKey);
        var ephemeralPublicKey = privateKey.GeneratePublicKey().GetEncoded();

        var sharedSecret = new byte[KeyLength];
        try
        {
            var agreement = new X25519Agreement();
            agreement.Init(privateKey);
            agreement.CalculateAgreement(new X25519PublicKeyParameters(recipientPublicKey), sharedSecret, 0);
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            // X25519 rejects small-order points, whose agreement is all zeros — a key an
            // attacker could also derive. Surfaced as an argument error because the cause
            // is the device's registered key, and the caller drops that one recipient.
            throw new ArgumentException(
                "Device public key is not a usable X25519 public key.",
                nameof(recipientPublicKey),
                exception);
        }

        try
        {
            var ciphertext = EncryptXChaCha20Poly1305(sharedSecret, nonce, associatedData, plaintext);

            var wire = new byte[NonceLength + KeyLength + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, wire, 0, NonceLength);
            Buffer.BlockCopy(ephemeralPublicKey, 0, wire, NonceLength, KeyLength);
            Buffer.BlockCopy(ciphertext, 0, wire, NonceLength + KeyLength, ciphertext.Length);
            return EncodeBase64Url(wire);
        }
        finally
        {
            // The shared secret is the AEAD key. Nothing else should be able to find it
            // in a heap dump or a swapped page after the send completes.
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>
    /// The authenticated associated data: <c>version|sender|recipient|keyId|eventId|expiresAt</c>.
    /// </summary>
    internal static byte[] BuildAssociatedData(
        string version,
        string senderInstallationId,
        string deviceId,
        string keyId,
        string clientEventId,
        string expiresAt) =>
        Encoding.UTF8.GetBytes(
            $"{version}|{senderInstallationId}|{deviceId}|{keyId}|{clientEventId}|{expiresAt}");

    /// <summary>
    /// XChaCha20-Poly1305: an HChaCha20 subkey from the first 16 nonce bytes, then the
    /// IETF AEAD with a 12-byte nonce of four zero bytes and the remaining eight.
    /// </summary>
    private static byte[] EncryptXChaCha20Poly1305(
        byte[] key,
        byte[] nonce24,
        byte[] associatedData,
        byte[] plaintext)
    {
        var subKey = HChaCha20(key, nonce24.AsSpan(0, 16));
        try
        {
            var nonce12 = new byte[12];
            nonce24.AsSpan(16, 8).CopyTo(nonce12.AsSpan(4));

            // Aliased: System.Security.Cryptography has a same-named type that is the
            // IETF AEAD too, but is OS-backed and not available everywhere.
            var cipher = new BcChaCha20Poly1305();
            cipher.Init(true, new AeadParameters(new KeyParameter(subKey), TagLength * 8, nonce12, associatedData));

            var output = new byte[cipher.GetOutputSize(plaintext.Length)];
            var written = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            cipher.DoFinal(output, written);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subKey);
        }
    }

    /// <summary>
    /// HChaCha20 (RFC 8439 §2.3 rounds, XChaCha20 draft §2.2): twenty rounds over the
    /// state, then the first and last four words taken directly — with no addition of the
    /// original state, which is what distinguishes it from the ChaCha20 block function.
    /// </summary>
    private static byte[] HChaCha20(byte[] key, ReadOnlySpan<byte> nonce16)
    {
        Span<uint> state = stackalloc uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);
        for (var i = 0; i < 4; i++)
            state[12 + i] = BitConverter.ToUInt32(nonce16.Slice(i * 4, 4));

        for (var round = 0; round < 10; round++)
        {
            QuarterRound(state, 0, 4, 8, 12);
            QuarterRound(state, 1, 5, 9, 13);
            QuarterRound(state, 2, 6, 10, 14);
            QuarterRound(state, 3, 7, 11, 15);
            QuarterRound(state, 0, 5, 10, 15);
            QuarterRound(state, 1, 6, 11, 12);
            QuarterRound(state, 2, 7, 8, 13);
            QuarterRound(state, 3, 4, 9, 14);
        }

        var subKey = new byte[KeyLength];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.TryWriteBytes(subKey.AsSpan(i * 4), state[i]);
            BitConverter.TryWriteBytes(subKey.AsSpan(16 + (i * 4)), state[12 + i]);
        }
        return subKey;
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 7);
    }

    private static uint RotateLeft(uint value, int offset) => (value << offset) | (value >> (32 - offset));

    internal static string EncodeBase64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static byte[] DecodeBase64Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must not be empty.", nameof(value));

        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        // Base64 needs the padding that base64url drops.
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            0 => normalized,
            _ => throw new FormatException("Value is not valid base64url."),
        };
        return Convert.FromBase64String(normalized);
    }
}
