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
        new(key ?? _key, _dir, NullLogger<EncryptedFilePepperStore>.Instance);

    private static StoredPepper SamplePepper(string siteId = "site-1", string epoch = "2026-07") =>
        new(siteId, epoch, Convert.ToBase64String(PepperGenerator.Generate()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var store = NewStore();
        var pepper = SamplePepper();
        await store.SaveAsync(pepper);

        var loaded = await store.GetAsync("site-1");

        Assert.NotNull(loaded);
        Assert.Equal(pepper.PepperBase64, loaded!.PepperBase64);
        Assert.Equal("2026-07", loaded.Epoch);
    }

    [Fact]
    public async Task Get_UnknownSite_ReturnsNull()
    {
        Assert.Null(await NewStore().GetAsync("nobody"));
    }

    [Fact]
    public async Task Save_OverwritesPriorPepper()
    {
        var store = NewStore();
        var first = SamplePepper(epoch: "2026-07");
        var second = SamplePepper(epoch: "2026-08");
        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var loaded = await store.GetAsync("site-1");

        Assert.Equal(second.PepperBase64, loaded!.PepperBase64);
        Assert.Equal("2026-08", loaded.Epoch);
    }

    [Fact]
    public async Task Delete_RemovesPepper()
    {
        var store = NewStore();
        await store.SaveAsync(SamplePepper());
        await store.DeleteAsync("site-1");

        Assert.Null(await store.GetAsync("site-1"));
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

        // The plaintext pepper (and site id) must not appear in the encrypted-at-rest bytes.
        Assert.DoesNotContain(pepper.PepperBase64, Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("site-1", Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public async Task WrongKey_CannotDecrypt()
    {
        await NewStore().SaveAsync(SamplePepper());

        var otherKeyStore = NewStore(RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherKeyStore.GetAsync("site-1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
