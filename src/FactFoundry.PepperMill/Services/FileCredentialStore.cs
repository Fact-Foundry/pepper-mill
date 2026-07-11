using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// File-backed <see cref="ICredentialStore"/>. One JSON file per tenant, named by a hash of the tenant
/// id (so the id never appears in a path). Records hold only a credential hash and non-secret metadata,
/// so — unlike the pepper store — they are not encrypted: there is nothing secret to protect at rest.
/// Files live in a <c>credentials</c> subdirectory of the store path, separate from the <c>.pepper</c> files.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store, ensuring its directory exists.</summary>
    public FileCredentialStore(IOptions<PepperMillOptions> options)
    {
        _directory = Path.Combine(options.Value.StorePath, "credentials");
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async Task<TenantCredential?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId);
        if (!File.Exists(path))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<TenantCredential>(bytes, JsonOptions)
                ?? throw new InvalidOperationException("Credential record was empty.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(TenantCredential credential, CancellationToken cancellationToken = default)
    {
        var path = PathFor(credential.TenantId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, payload, cancellationToken);
            File.Move(temp, path, overwrite: true); // atomic replace
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId);
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

    private string PathFor(string tenantId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)));
        return Path.Combine(_directory, hash + ".cred");
    }
}
