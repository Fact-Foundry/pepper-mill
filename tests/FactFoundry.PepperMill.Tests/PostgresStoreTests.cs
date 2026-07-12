using System.Security.Cryptography;
using System.Text;
using FactFoundry.PepperMill.Services;
using Npgsql;

namespace FactFoundry.PepperMill.Tests;

/// <summary>
/// Integration tests for the Postgres backend. These exercise the real SQL, so they run ONLY when the
/// environment variable <c>PEPPERMILL_TEST_POSTGRES</c> holds a connection string (CI, or a machine with
/// Postgres). Without it they no-op — a fake database cannot validate real SQL, so there is nothing to
/// assert here otherwise. Each test namespaces its rows by a unique clusterId and cleans up after itself.
/// </summary>
public class PostgresStoreTests
{
    private static string? ConnString => Environment.GetEnvironmentVariable("PEPPERMILL_TEST_POSTGRES");

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static StoredPepper SamplePepper(string cluster, string epoch = "2026-07") =>
        new(cluster, "tenant-1", "site-1", epoch, Convert.ToBase64String(PepperGenerator.Generate()), DateTimeOffset.UtcNow);

    private static SiteCredential SampleCred(string cluster, string hash = "hash-1") =>
        new(cluster, "tenant-1", "site-1", hash, "https://cb.internal/x", RotationIntervalDays: null, Locked: true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Pepper_RoundTrips_Overwrites_Deletes()
    {
        var conn = ConnString;
        if (conn is null) return; // skipped: no Postgres configured

        var cluster = "test-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(conn);
        await PostgresSchema.EnsureAsync(ds);
        var store = new PostgresPepperStore(ds, new PepperCipher(Key));
        try
        {
            var p1 = SamplePepper(cluster, epoch: "2026-07");
            await store.SaveAsync(p1);
            var loaded = await store.GetAsync(cluster, "tenant-1", "site-1");
            Assert.NotNull(loaded);
            Assert.Equal(p1.PepperBase64, loaded!.PepperBase64);
            Assert.Equal("2026-07", loaded.Epoch);

            // Upsert overwrites, destroying the prior epoch's pepper.
            var p2 = SamplePepper(cluster, epoch: "2026-08");
            await store.SaveAsync(p2);
            var reloaded = await store.GetAsync(cluster, "tenant-1", "site-1");
            Assert.Equal(p2.PepperBase64, reloaded!.PepperBase64);
            Assert.Equal("2026-08", reloaded.Epoch);

            await store.DeleteAsync(cluster, "tenant-1", "site-1");
            Assert.Null(await store.GetAsync(cluster, "tenant-1", "site-1"));
        }
        finally
        {
            await Cleanup(ds, cluster);
        }
    }

    [Fact]
    public async Task Credential_RoundTrips_Overwrites_Deletes()
    {
        var conn = ConnString;
        if (conn is null) return; // skipped: no Postgres configured

        var cluster = "test-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(conn);
        await PostgresSchema.EnsureAsync(ds);
        var store = new PostgresCredentialStore(ds);
        try
        {
            await store.SaveAsync(SampleCred(cluster, hash: "old"));
            var loaded = await store.GetAsync(cluster, "tenant-1", "site-1");
            Assert.NotNull(loaded);
            Assert.Equal("old", loaded!.Key2Hash);
            Assert.True(loaded.Locked);

            await store.SaveAsync(SampleCred(cluster, hash: "new"));
            Assert.Equal("new", (await store.GetAsync(cluster, "tenant-1", "site-1"))!.Key2Hash);

            await store.DeleteAsync(cluster, "tenant-1", "site-1");
            Assert.Null(await store.GetAsync(cluster, "tenant-1", "site-1"));
        }
        finally
        {
            await Cleanup(ds, cluster);
        }
    }

    [Fact]
    public async Task Pepper_StoredValue_IsCiphertext_NotPlaintext()
    {
        var conn = ConnString;
        if (conn is null) return; // skipped: no Postgres configured

        var cluster = "test-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(conn);
        await PostgresSchema.EnsureAsync(ds);
        var store = new PostgresPepperStore(ds, new PepperCipher(Key));
        try
        {
            var p = SamplePepper(cluster);
            await store.SaveAsync(p);

            await using var cmd = ds.CreateCommand("SELECT ciphertext FROM peppers WHERE cluster_id = $1");
            cmd.Parameters.Add(new() { Value = cluster });
            var raw = (byte[])(await cmd.ExecuteScalarAsync())!;

            // The database holds only ciphertext — the plaintext pepper never appears.
            Assert.DoesNotContain(p.PepperBase64, Encoding.UTF8.GetString(raw));
        }
        finally
        {
            await Cleanup(ds, cluster);
        }
    }

    private static async Task Cleanup(NpgsqlDataSource ds, string cluster)
    {
        await using var c1 = ds.CreateCommand("DELETE FROM peppers WHERE cluster_id = $1");
        c1.Parameters.Add(new() { Value = cluster });
        await c1.ExecuteNonQueryAsync();
        await using var c2 = ds.CreateCommand("DELETE FROM credentials WHERE cluster_id = $1");
        c2.Parameters.Add(new() { Value = cluster });
        await c2.ExecuteNonQueryAsync();
    }
}
