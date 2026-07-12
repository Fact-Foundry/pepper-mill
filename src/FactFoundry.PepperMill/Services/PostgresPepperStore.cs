using Npgsql;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Postgres-backed <see cref="IPepperStore"/>. Peppers are encrypted app-side with the shared
/// <see cref="PepperCipher"/> and stored as a <c>bytea</c> blob keyed by <c>(cluster, tenant, site)</c>,
/// so the database only ever holds ciphertext — the master key stays outside it. The plaintext
/// <c>epoch</c> column (not secret) enables the rotation sweep to filter without decrypting.
/// </summary>
public sealed class PostgresPepperStore : IPepperStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PepperCipher _cipher;

    /// <summary>Creates the store over a data source and the shared cipher.</summary>
    public PostgresPepperStore(NpgsqlDataSource dataSource, PepperCipher cipher)
    {
        _dataSource = dataSource;
        _cipher = cipher;
    }

    /// <inheritdoc />
    public async Task<StoredPepper?> GetAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT ciphertext FROM peppers WHERE cluster_id = $1 AND tenant_id = $2 AND site_id = $3");
        command.Parameters.Add(new() { Value = clusterId });
        command.Parameters.Add(new() { Value = tenantId });
        command.Parameters.Add(new() { Value = siteId });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is byte[] ciphertext ? _cipher.Decrypt(ciphertext) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredPepper pepper, CancellationToken cancellationToken = default)
    {
        // Upsert = atomic overwrite; replacing the row destroys any prior epoch's pepper.
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO peppers (cluster_id, tenant_id, site_id, epoch, ciphertext, created_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (cluster_id, tenant_id, site_id)
            DO UPDATE SET epoch = EXCLUDED.epoch, ciphertext = EXCLUDED.ciphertext, created_at = EXCLUDED.created_at
            """);
        command.Parameters.Add(new() { Value = pepper.ClusterId });
        command.Parameters.Add(new() { Value = pepper.TenantId });
        command.Parameters.Add(new() { Value = pepper.SiteId });
        command.Parameters.Add(new() { Value = pepper.Epoch });
        command.Parameters.Add(new() { Value = _cipher.Encrypt(pepper) });
        command.Parameters.Add(new() { Value = pepper.CreatedAtUtc });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM peppers WHERE cluster_id = $1 AND tenant_id = $2 AND site_id = $3");
        command.Parameters.Add(new() { Value = clusterId });
        command.Parameters.Add(new() { Value = tenantId });
        command.Parameters.Add(new() { Value = siteId });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredPepper>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<StoredPepper>();
        await using var command = _dataSource.CreateCommand("SELECT ciphertext FROM peppers");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(_cipher.Decrypt(reader.GetFieldValue<byte[]>(0)));
        return result;
    }
}
