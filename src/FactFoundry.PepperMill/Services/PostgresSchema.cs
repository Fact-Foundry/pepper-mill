using Npgsql;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Creates the Postgres schema idempotently. Every statement is <c>IF NOT EXISTS</c>-guarded, so this
/// can be run on every startup against a fresh, partial, or fully-built database and always converges
/// to the target — no migration engine or history table needed for this small, stable schema.
/// </summary>
public static class PostgresSchema
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS peppers (
            cluster_id  text        NOT NULL,
            tenant_id   text        NOT NULL,
            site_id     text        NOT NULL,
            epoch       text        NOT NULL,
            ciphertext  bytea       NOT NULL,
            created_at  timestamptz NOT NULL,
            PRIMARY KEY (cluster_id, tenant_id, site_id)
        );

        CREATE INDEX IF NOT EXISTS ix_peppers_epoch ON peppers (epoch);

        CREATE TABLE IF NOT EXISTS credentials (
            cluster_id             text        NOT NULL,
            tenant_id              text        NOT NULL,
            site_id                text        NOT NULL,
            key2_hash              text        NOT NULL,
            callback_url           text        NOT NULL,
            rotation_interval_days integer     NULL,
            locked                 boolean     NOT NULL,
            created_at             timestamptz NOT NULL,
            PRIMARY KEY (cluster_id, tenant_id, site_id)
        );
        """;

    /// <summary>Ensures the peppers and credentials tables exist. Safe to call repeatedly.</summary>
    public static async Task EnsureAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(Ddl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
