using System.Security.Cryptography;
using System.Text.Json;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// AES-256-GCM cipher for pepper records, shared by every storage backend so peppers are encrypted
/// identically wherever they land — file, database, or object store. The master key lives outside the
/// store, so a stored blob without it is useless; this is what keeps that property true regardless of
/// backend. Output layout is <c>nonce ‖ tag ‖ ciphertext</c>.
/// </summary>
public sealed class PepperCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly byte[] _masterKey;

    /// <summary>Creates the cipher from the 32-byte AES-256 master key.</summary>
    public PepperCipher(byte[] masterKey)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("The pepper master key must be exactly 32 bytes.", nameof(masterKey));
        _masterKey = masterKey;
    }

    /// <summary>Serializes and AES-256-GCM-encrypts a pepper record into <c>nonce ‖ tag ‖ ciphertext</c>.</summary>
    public byte[] Encrypt(StoredPepper pepper)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(pepper, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    /// <summary>Reverses <see cref="Encrypt"/>: decrypts and deserializes a stored pepper record.</summary>
    public StoredPepper Decrypt(byte[] payload)
    {
        var nonce = payload[..NonceSize];
        var tag = payload[NonceSize..(NonceSize + TagSize)];
        var ciphertext = payload[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return JsonSerializer.Deserialize<StoredPepper>(plaintext, JsonOptions)
            ?? throw new InvalidOperationException("Decrypted pepper record was empty.");
    }
}
