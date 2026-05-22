using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;

namespace Stashboard.Infrastructure.Security;

/// <summary>AES-256-GCM authenticated encryption. Layout: nonce(12) | tag(16) | ciphertext.</summary>
public sealed class AesEncryptionService : IEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesEncryptionService(IOptions<EncryptionOptions> options)
    {
        var raw = options.Value.Key;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Encryption:Key is not configured. Provide a Base64-encoded 32-byte key (e.g., via STASHBOARD_ENCRYPTION_KEY env var).");

        _key = Convert.FromBase64String(raw);
        if (_key.Length != 32)
            throw new InvalidOperationException($"Encryption key must be 32 bytes (256 bits); got {_key.Length}.");
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var input = Convert.FromBase64String(ciphertext);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short.");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[input.Length - NonceSize - TagSize];
        Buffer.BlockCopy(input, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(input, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(input, NonceSize + TagSize, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Helper for setup: generates a fresh Base64 32-byte key.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
