using FactFoundry.PepperMill;
using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Tests;

public class FileCredentialStoreTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pm-cred-" + Guid.NewGuid());

    private FileCredentialStore NewStore() =>
        new(Options.Create(new PepperMillOptions { StorePath = _storeDir }));

    private static TenantCredential Sample(string tenantId = "tenant-1", string key2Hash = "abc123", bool locked = true) =>
        new(tenantId, key2Hash, "https://tf.internal/pepper-callback", RotationIntervalDays: null, locked, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var store = NewStore();
        var cred = Sample();
        await store.SaveAsync(cred);

        var loaded = await store.GetAsync("tenant-1");

        Assert.NotNull(loaded);
        Assert.Equal(cred.Key2Hash, loaded!.Key2Hash);
        Assert.Equal(cred.CallbackUrl, loaded.CallbackUrl);
        Assert.True(loaded.Locked);
    }

    [Fact]
    public async Task Get_UnknownTenant_ReturnsNull()
    {
        Assert.Null(await NewStore().GetAsync("nobody"));
    }

    [Fact]
    public async Task Save_OverwritesPriorRecord()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(key2Hash: "old"));
        await store.SaveAsync(Sample(key2Hash: "new"));

        var loaded = await store.GetAsync("tenant-1");

        Assert.Equal("new", loaded!.Key2Hash);
    }

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        var store = NewStore();
        await store.SaveAsync(Sample());
        await store.DeleteAsync("tenant-1");

        Assert.Null(await store.GetAsync("tenant-1"));
    }

    [Fact]
    public async Task DistinctTenants_AreIsolated()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("tenant-a", "hash-a"));
        await store.SaveAsync(Sample("tenant-b", "hash-b"));

        Assert.Equal("hash-a", (await store.GetAsync("tenant-a"))!.Key2Hash);
        Assert.Equal("hash-b", (await store.GetAsync("tenant-b"))!.Key2Hash);
    }

    [Fact]
    public async Task FileOnDisk_DoesNotContainTheTenantIdInItsPath()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("acme-corp"));

        var file = Directory.EnumerateFiles(Path.Combine(_storeDir, "credentials"), "*.cred").Single();

        // The tenant id must not appear in the file name (it is hashed).
        Assert.DoesNotContain("acme-corp", Path.GetFileName(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }
}
