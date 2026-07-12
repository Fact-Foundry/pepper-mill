using System.Security.Cryptography;
using System.Text;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A file-backed <see cref="IPepperStore"/>. Each site's pepper is encrypted with the shared
/// <see cref="PepperCipher"/> (AES-256-GCM under a master key held outside the store) and written as
/// one file per (cluster, tenant, site) triple — named by a hash of the composite id, so no id ever
/// appears in a path. A copy of the files alone is useless without the master key.
/// </summary>
public sealed class EncryptedFilePepperStore : IPepperStore
{
    private readonly PepperCipher _cipher;
    private readonly string _directory;
    private readonly ILogger<EncryptedFilePepperStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store, ensuring its directory exists.</summary>
    /// <param name="cipher">The shared pepper cipher (holds the master key).</param>
    /// <param name="directory">Directory holding the encrypted pepper files.</param>
    /// <param name="logger">Logger (never receives pepper material).</param>
    public EncryptedFilePepperStore(PepperCipher cipher, string directory, ILogger<EncryptedFilePepperStore> logger)
    {
        _cipher = cipher;
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async Task<StoredPepper?> GetAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(clusterId, tenantId, siteId);
        if (!File.Exists(path))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return _cipher.Decrypt(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredPepper pepper, CancellationToken cancellationToken = default)
    {
        var path = PathFor(pepper.ClusterId, pepper.TenantId, pepper.SiteId);
        var payload = _cipher.Encrypt(pepper);

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
    public async Task DeleteAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(clusterId, tenantId, siteId);
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
                result.Add(_cipher.Decrypt(bytes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read pepper file {File}; skipping", Path.GetFileName(file));
            }
        }
        return result;
    }

    /// <summary>Maps a (cluster, tenant, site) triple to its encrypted file path via a collision-resistant hash — the ids never appear in the path.</summary>
    private string PathFor(string clusterId, string tenantId, string siteId)
    {
        // Length-prefix each component so distinct triples can never collide onto the same file
        // (e.g. "ab"/"c" vs "a"/"bc"). No id appears in the path — only the hash.
        var composite = $"{clusterId.Length}:{clusterId}:{tenantId.Length}:{tenantId}:{siteId.Length}:{siteId}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        return Path.Combine(_directory, hash + ".pepper");
    }
}
