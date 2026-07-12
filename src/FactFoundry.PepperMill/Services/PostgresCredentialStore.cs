using Npgsql;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Postgres-backed <see cref="ICredentialStore"/>. One row per site keyed by
/// <c>(cluster, tenant, site)</c>, holding only a hash of the credential plus non-secret metadata —
/// nothing here needs encryption.
/// </summary>
public sealed class PostgresCredentialStore : ICredentialStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source.</summary>
    public PostgresCredentialStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<SiteCredential?> GetAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT key2_hash, callback_url, rotation_interval_days, locked, created_at
            FROM credentials WHERE cluster_id = $1 AND tenant_id = $2 AND site_id = $3
            """);
        command.Parameters.Add(new() { Value = clusterId });
        command.Parameters.Add(new() { Value = tenantId });
        command.Parameters.Add(new() { Value = siteId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new SiteCredential(
            clusterId,
            tenantId,
            siteId,
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <inheritdoc />
    public async Task SaveAsync(SiteCredential credential, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO credentials (cluster_id, tenant_id, site_id, key2_hash, callback_url, rotation_interval_days, locked, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (cluster_id, tenant_id, site_id)
            DO UPDATE SET key2_hash = EXCLUDED.key2_hash, callback_url = EXCLUDED.callback_url,
                          rotation_interval_days = EXCLUDED.rotation_interval_days, locked = EXCLUDED.locked,
                          created_at = EXCLUDED.created_at
            """);
        command.Parameters.Add(new() { Value = credential.ClusterId });
        command.Parameters.Add(new() { Value = credential.TenantId });
        command.Parameters.Add(new() { Value = credential.SiteId });
        command.Parameters.Add(new() { Value = credential.Key2Hash });
        command.Parameters.Add(new() { Value = credential.CallbackUrl });
        command.Parameters.Add(new() { Value = (object?)credential.RotationIntervalDays ?? DBNull.Value });
        command.Parameters.Add(new() { Value = credential.Locked });
        command.Parameters.Add(new() { Value = credential.CreatedAtUtc });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM credentials WHERE cluster_id = $1 AND tenant_id = $2 AND site_id = $3");
        command.Parameters.Add(new() { Value = clusterId });
        command.Parameters.Add(new() { Value = tenantId });
        command.Parameters.Add(new() { Value = siteId });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
