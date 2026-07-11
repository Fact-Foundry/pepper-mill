using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A file-backed <see cref="IPepperStore"/> that encrypts each site's pepper at rest with
/// AES-256-GCM under a master key held outside the store. One file per (tenant, site) pair (named
/// by a hash of the composite id, so neither id ever appears in a path); the file holds
/// <c>nonce ‖ tag ‖ ciphertext</c>. A copy of the files alone is useless without the master key.
/// </summary>
public sealed class EncryptedFilePepperStore : IPepperStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly byte[] _masterKey;
    private readonly string _directory;
    private readonly ILogger<EncryptedFilePepperStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store, ensuring its directory exists.</summary>
    /// <param name="masterKey">The 32-byte AES-256 master key.</param>
    /// <param name="directory">Directory holding the encrypted pepper files.</param>
    /// <param name="logger">Logger (never receives pepper material).</param>
    public EncryptedFilePepperStore(byte[] masterKey, string directory, ILogger<EncryptedFilePepperStore> logger)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("The pepper master key must be exactly 32 bytes.", nameof(masterKey));

        _masterKey = masterKey;
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async Task<StoredPepper?> GetAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId, siteId);
        if (!File.Exists(path))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Decrypt(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredPepper pepper, CancellationToken cancellationToken = default)
    {
        var path = PathFor(pepper.TenantId, pepper.SiteId);
        var payload = Encrypt(pepper);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, payload, cancellationToken);
            File.Move(temp, path, overwrite: true); // atomic replace destroys any prior epoch
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId, siteId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredPepper>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<StoredPepper>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.pepper"))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                result.Add(Decrypt(bytes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read pepper file {File}; skipping", Path.GetFileName(file));
            }
        }
        return result;
    }

    /// <summary>Maps a (tenant, site) pair to its encrypted file path via a collision-resistant hash — the ids never appear in the path.</summary>
    private string PathFor(string tenantId, string siteId)
    {
        // Length-prefix each component so distinct (tenant, site) pairs can never collide onto the
        // same file (e.g. "ab"/"c" vs "a"/"bc"). Neither id appears in the path — only the hash.
        var composite = $"{tenantId.Length}:{tenantId}:{siteId.Length}:{siteId}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        return Path.Combine(_directory, hash + ".pepper");
    }

    /// <summary>Serializes and AES-256-GCM-encrypts a pepper record into <c>nonce ‖ tag ‖ ciphertext</c>.</summary>
    private byte[] Encrypt(StoredPepper pepper)
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
    private StoredPepper Decrypt(byte[] payload)
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
