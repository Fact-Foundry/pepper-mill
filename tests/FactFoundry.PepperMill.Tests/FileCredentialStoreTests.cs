using FactFoundry.PepperMill;
using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Tests;

public class FileCredentialStoreTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pm-cred-" + Guid.NewGuid());

    private FileCredentialStore NewStore() =>
        new(Options.Create(new PepperMillOptions { StorePath = _storeDir }));

    private static SiteCredential Sample(string tenantId = "tenant-1", string siteId = "site-1", string key2Hash = "abc123", bool locked = true, string clusterId = "default") =>
        new(clusterId, tenantId, siteId, key2Hash, "https://tf.internal/pepper-callback", RotationIntervalDays: null, locked, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var store = NewStore();
        var cred = Sample();
        await store.SaveAsync(cred);

        var loaded = await store.GetAsync("default", "tenant-1", "site-1");

        Assert.NotNull(loaded);
        Assert.Equal(cred.Key2Hash, loaded!.Key2Hash);
        Assert.Equal(cred.CallbackUrl, loaded.CallbackUrl);
        Assert.True(loaded.Locked);
    }

    [Fact]
    public async Task Get_UnknownSite_ReturnsNull()
    {
        Assert.Null(await NewStore().GetAsync("default", "tenant-1", "nobody"));
    }

    [Fact]
    public async Task Save_OverwritesPriorRecord()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(key2Hash: "old"));
        await store.SaveAsync(Sample(key2Hash: "new"));

        var loaded = await store.GetAsync("default", "tenant-1", "site-1");

        Assert.Equal("new", loaded!.Key2Hash);
    }

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        var store = NewStore();
        await store.SaveAsync(Sample());
        await store.DeleteAsync("default", "tenant-1", "site-1");

        Assert.Null(await store.GetAsync("default", "tenant-1", "site-1"));
    }

    [Fact]
    public async Task SitesUnderOneTenant_HaveIndependentCredentials()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(siteId: "blog", key2Hash: "hash-blog"));
        await store.SaveAsync(Sample(siteId: "shop", key2Hash: "hash-shop"));

        Assert.Equal("hash-blog", (await store.GetAsync("default", "tenant-1", "blog"))!.Key2Hash);
        Assert.Equal("hash-shop", (await store.GetAsync("default", "tenant-1", "shop"))!.Key2Hash);
    }

    [Fact]
    public async Task SameSiteId_DifferentTenants_AreIsolated()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(tenantId: "tenant-a", siteId: "shared", key2Hash: "hash-a"));
        await store.SaveAsync(Sample(tenantId: "tenant-b", siteId: "shared", key2Hash: "hash-b"));

        Assert.Equal("hash-a", (await store.GetAsync("default", "tenant-a", "shared"))!.Key2Hash);
        Assert.Equal("hash-b", (await store.GetAsync("default", "tenant-b", "shared"))!.Key2Hash);
    }

    [Fact]
    public async Task FileOnDisk_DoesNotContainTheIdsInItsPath()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("acme-corp", "acme-blog"));

        var file = Directory.EnumerateFiles(Path.Combine(_storeDir, "credentials"), "*.cred").Single();

        // Neither id appears in the file name (they are hashed).
        Assert.DoesNotContain("acme-corp", Path.GetFileName(file));
        Assert.DoesNotContain("acme-blog", Path.GetFileName(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }
}
