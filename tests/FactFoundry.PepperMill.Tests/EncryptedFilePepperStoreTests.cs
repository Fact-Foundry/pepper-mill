using System.Security.Cryptography;
using System.Text;
using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactFoundry.PepperMill.Tests;

public class EncryptedFilePepperStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-store-" + Guid.NewGuid());
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private EncryptedFilePepperStore NewStore(byte[]? key = null) =>
        new(new PepperCipher(key ?? _key), _dir, NullLogger<EncryptedFilePepperStore>.Instance);

    private const string Tenant = "tenant-1";
    private const string Cluster = "default";

    private static StoredPepper SamplePepper(string siteId = "site-1", string epoch = "2026-07", string tenantId = Tenant, string clusterId = Cluster) =>
        new(clusterId, tenantId, siteId, epoch, Convert.ToBase64String(PepperGenerator.Generate()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var store = NewStore();
        var pepper = SamplePepper();
        await store.SaveAsync(pepper);

        var loaded = await store.GetAsync(Cluster, Tenant, "site-1");

        Assert.NotNull(loaded);
        Assert.Equal(pepper.PepperBase64, loaded!.PepperBase64);
        Assert.Equal("2026-07", loaded.Epoch);
    }

    [Fact]
    public async Task Get_UnknownSite_ReturnsNull()
    {
        Assert.Null(await NewStore().GetAsync(Cluster, Tenant, "nobody"));
    }

    [Fact]
    public async Task SameSiteId_DifferentTenants_AreIsolated()
    {
        var store = NewStore();
        var a = SamplePepper(siteId: "shared", tenantId: "tenant-a");
        var b = SamplePepper(siteId: "shared", tenantId: "tenant-b");
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        // A same-named site under a different tenant is a distinct pepper — no cross-tenant bleed.
        Assert.Equal(a.PepperBase64, (await store.GetAsync(Cluster, "tenant-a", "shared"))!.PepperBase64);
        Assert.Equal(b.PepperBase64, (await store.GetAsync(Cluster, "tenant-b", "shared"))!.PepperBase64);
        Assert.NotEqual(a.PepperBase64, b.PepperBase64);
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task SameSiteId_DifferentClusters_AreIsolated()
    {
        var store = NewStore();
        var a = SamplePepper(siteId: "shared", tenantId: "acme", clusterId: "cluster-a");
        var b = SamplePepper(siteId: "shared", tenantId: "acme", clusterId: "cluster-b");
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        // Same tenant/site under a different cluster is a distinct pepper — clusters are segregated.
        Assert.Equal(a.PepperBase64, (await store.GetAsync("cluster-a", "acme", "shared"))!.PepperBase64);
        Assert.Equal(b.PepperBase64, (await store.GetAsync("cluster-b", "acme", "shared"))!.PepperBase64);
        Assert.NotEqual(a.PepperBase64, b.PepperBase64);
    }

    [Fact]
    public async Task Save_OverwritesPriorPepper()
    {
        var store = NewStore();
        var first = SamplePepper(epoch: "2026-07");
        var second = SamplePepper(epoch: "2026-08");
        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var loaded = await store.GetAsync(Cluster, Tenant, "site-1");

        Assert.Equal(second.PepperBase64, loaded!.PepperBase64);
        Assert.Equal("2026-08", loaded.Epoch);
    }

    [Fact]
    public async Task Delete_RemovesPepper()
    {
        var store = NewStore();
        await store.SaveAsync(SamplePepper());
        await store.DeleteAsync(Cluster, Tenant, "site-1");

        Assert.Null(await store.GetAsync(Cluster, Tenant, "site-1"));
    }

    [Fact]
    public async Task List_ReturnsAllStored()
    {
        var store = NewStore();
        await store.SaveAsync(SamplePepper("a"));
        await store.SaveAsync(SamplePepper("b"));

        var all = await store.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.SiteId == "a");
        Assert.Contains(all, p => p.SiteId == "b");
    }

    [Fact]
    public async Task FileOnDisk_DoesNotContainThePepper()
    {
        var store = NewStore();
        var pepper = SamplePepper();
        await store.SaveAsync(pepper);

        var file = Directory.EnumerateFiles(_dir, "*.pepper").Single();
        var raw = await File.ReadAllBytesAsync(file);

        // The plaintext pepper (and tenant/site ids) must not appear in the encrypted-at-rest bytes.
        Assert.DoesNotContain(pepper.PepperBase64, Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("site-1", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain(Tenant, Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public async Task WrongKey_CannotDecrypt()
    {
        await NewStore().SaveAsync(SamplePepper());

        var otherKeyStore = NewStore(RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherKeyStore.GetAsync(Cluster, Tenant, "site-1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
