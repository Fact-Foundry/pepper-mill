using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Tests;

public class LocalEntitlementProviderTests
{
    private static TenantCredential Enrolled(string tenantId, string key2) =>
        new(tenantId, CredentialHasher.Hash(key2), "https://cb.internal/hook", RotationIntervalDays: null, Locked: true, DateTimeOffset.UtcNow);

    private static async Task<LocalEntitlementProvider> ProviderWith(params TenantCredential[] credentials)
    {
        var store = new InMemoryCredentialStore();
        foreach (var c in credentials)
            await store.SaveAsync(c);
        return new LocalEntitlementProvider(store);
    }

    [Fact]
    public async Task EnrolledTenant_CorrectCredential_IsEntitled()
    {
        var provider = await ProviderWith(Enrolled("acme", "the-key2"));

        Assert.True(await provider.IsEntitledAsync("the-key2", "acme", "any-site"));
    }

    [Fact]
    public async Task WrongCredential_IsNotEntitled()
    {
        var provider = await ProviderWith(Enrolled("acme", "the-key2"));

        Assert.False(await provider.IsEntitledAsync("guessing", "acme", "any-site"));
    }

    [Fact]
    public async Task UnenrolledTenant_IsNotEntitled()
    {
        var provider = new LocalEntitlementProvider(new InMemoryCredentialStore());

        Assert.False(await provider.IsEntitledAsync("anything", "nobody", "any-site"));
    }

    [Fact]
    public async Task CredentialForOneTenant_CannotClaimAnother()
    {
        var provider = await ProviderWith(Enrolled("tenant-a", "a-key2"), Enrolled("tenant-b", "b-key2"));

        // A's credential works for A but not for B — changing the body tenantId does not help.
        Assert.True(await provider.IsEntitledAsync("a-key2", "tenant-a", "site"));
        Assert.False(await provider.IsEntitledAsync("a-key2", "tenant-b", "site"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task EmptyCredential_IsNotEntitled(string? credential)
    {
        var provider = await ProviderWith(Enrolled("acme", "the-key2"));

        Assert.False(await provider.IsEntitledAsync(credential!, "acme", "any-site"));
    }
}
