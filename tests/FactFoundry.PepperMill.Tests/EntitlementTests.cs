using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Tests;

public class LocalEntitlementProviderTests
{
    private static SiteCredential Registered(string tenantId, string siteId, string key2, string clusterId = "default") =>
        new(clusterId, tenantId, siteId, CredentialHasher.Hash(key2), "https://cb.internal/hook", RotationIntervalDays: null, Locked: true, DateTimeOffset.UtcNow);

    private static async Task<LocalEntitlementProvider> ProviderWith(params SiteCredential[] credentials)
    {
        var store = new InMemoryCredentialStore();
        foreach (var c in credentials)
            await store.SaveAsync(c);
        return new LocalEntitlementProvider(store);
    }

    [Fact]
    public async Task RegisteredSite_CorrectCredential_IsEntitled()
    {
        var provider = await ProviderWith(Registered("acme", "blog", "the-key2"));

        Assert.True(await provider.IsEntitledAsync("the-key2", "default", "acme", "blog"));
    }

    [Fact]
    public async Task WrongCredential_IsNotEntitled()
    {
        var provider = await ProviderWith(Registered("acme", "blog", "the-key2"));

        Assert.False(await provider.IsEntitledAsync("guessing", "default", "acme", "blog"));
    }

    [Fact]
    public async Task UnregisteredSite_IsNotEntitled()
    {
        var provider = new LocalEntitlementProvider(new InMemoryCredentialStore());

        Assert.False(await provider.IsEntitledAsync("anything", "default", "acme", "nobody"));
    }

    [Fact]
    public async Task CredentialForOneSite_CannotClaimAnotherSite()
    {
        // Two sites under the same tenant, each with its own credential.
        var provider = await ProviderWith(
            Registered("acme", "blog", "blog-key2"),
            Registered("acme", "shop", "shop-key2"));

        // blog's credential works for blog but not for shop — the leak is scoped to one site.
        Assert.True(await provider.IsEntitledAsync("blog-key2", "default", "acme", "blog"));
        Assert.False(await provider.IsEntitledAsync("blog-key2", "default", "acme", "shop"));
    }

    [Fact]
    public async Task CredentialForOneTenant_CannotClaimAnotherTenant()
    {
        var provider = await ProviderWith(
            Registered("tenant-a", "site", "a-key2"),
            Registered("tenant-b", "site", "b-key2"));

        Assert.True(await provider.IsEntitledAsync("a-key2", "default", "tenant-a", "site"));
        Assert.False(await provider.IsEntitledAsync("a-key2", "default", "tenant-b", "site"));
    }

    [Fact]
    public async Task CredentialForOneCluster_CannotClaimAnother()
    {
        // Same tenant/site name in two clusters, each with its own credential — no cross-cluster bleed.
        var provider = await ProviderWith(
            Registered("acme", "blog", "a-key2", clusterId: "cluster-a"),
            Registered("acme", "blog", "b-key2", clusterId: "cluster-b"));

        Assert.True(await provider.IsEntitledAsync("a-key2", "cluster-a", "acme", "blog"));
        Assert.False(await provider.IsEntitledAsync("a-key2", "cluster-b", "acme", "blog"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task EmptyCredential_IsNotEntitled(string? credential)
    {
        var provider = await ProviderWith(Registered("acme", "blog", "the-key2"));

        Assert.False(await provider.IsEntitledAsync(credential!, "default", "acme", "blog"));
    }
}
