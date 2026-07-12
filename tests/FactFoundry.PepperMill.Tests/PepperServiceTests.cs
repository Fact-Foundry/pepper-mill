using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Tests;

public class PepperServiceTests
{
    private static readonly DateTimeOffset July = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset August = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
    private const string Tenant = "tenant-1";
    private const string Cluster = "default";

    [Fact]
    public async Task GetCurrent_GeneratesAndPersists()
    {
        var store = new InMemoryPepperStore();
        var service = new PepperService(store, new TestClock(July));

        var result = await service.GetCurrentAsync(Cluster, Tenant, "site-1");

        Assert.Equal("2026-07", result.Epoch);
        Assert.False(string.IsNullOrEmpty(result.PepperBase64));
        Assert.NotNull(await store.GetAsync(Cluster, Tenant, "site-1"));
    }

    [Fact]
    public async Task GetCurrent_SameEpoch_ReturnsSamePepper()
    {
        var service = new PepperService(new InMemoryPepperStore(), new TestClock(July));

        var first = await service.GetCurrentAsync(Cluster, Tenant, "site-1");
        var second = await service.GetCurrentAsync(Cluster, Tenant, "site-1");

        Assert.Equal(first.PepperBase64, second.PepperBase64);
    }

    [Fact]
    public async Task GetCurrent_SameSiteId_DifferentTenants_GetDistinctPeppers()
    {
        var service = new PepperService(new InMemoryPepperStore(), new TestClock(July));

        var a = await service.GetCurrentAsync(Cluster, "tenant-a", "shared");
        var b = await service.GetCurrentAsync(Cluster, "tenant-b", "shared");

        Assert.NotEqual(a.PepperBase64, b.PepperBase64);
    }

    [Fact]
    public async Task GetCurrent_EpochAdvances_RotatesToNewPepper()
    {
        var clock = new TestClock(July);
        var service = new PepperService(new InMemoryPepperStore(), clock);

        var julyPepper = await service.GetCurrentAsync(Cluster, Tenant, "site-1");
        clock.UtcNow = August;
        var augustPepper = await service.GetCurrentAsync(Cluster, Tenant, "site-1");

        Assert.Equal("2026-08", augustPepper.Epoch);
        Assert.NotEqual(julyPepper.PepperBase64, augustPepper.PepperBase64);
    }

    [Fact]
    public async Task RotateStale_RegeneratesOnlyStaleEpochs()
    {
        var clock = new TestClock(July);
        var store = new InMemoryPepperStore();
        var service = new PepperService(store, clock);
        var julyPepper = await service.GetCurrentAsync(Cluster, Tenant, "site-1");

        clock.UtcNow = August;
        var rotated = await service.RotateStaleAsync();

        Assert.Equal(1, rotated);
        var stored = await store.GetAsync(Cluster, Tenant, "site-1");
        Assert.Equal("2026-08", stored!.Epoch);
        Assert.NotEqual(julyPepper.PepperBase64, stored.PepperBase64);

        // Running again in the same epoch rotates nothing.
        Assert.Equal(0, await service.RotateStaleAsync());
    }
}
