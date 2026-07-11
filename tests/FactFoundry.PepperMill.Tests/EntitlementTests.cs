using FactFoundry.PepperMill;
using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Tests;

public class LocalEntitlementProviderTests
{
    private static LocalEntitlementProvider Provider(string? configured) =>
        new(Options.Create(new PepperMillOptions { LocalServerCredential = configured }));

    [Fact]
    public async Task ValidCredential_IsEntitled()
    {
        var provider = Provider("s3cret-server-credential");

        Assert.True(await provider.IsEntitledAsync("s3cret-server-credential", "any-tenant", "any-site"));
    }

    [Fact]
    public async Task WrongCredential_IsNotEntitled()
    {
        var provider = Provider("s3cret-server-credential");

        Assert.False(await provider.IsEntitledAsync("guessing", "any-tenant", "any-site"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Unconfigured_DeniesEverything(string? configured)
    {
        var provider = Provider(configured);

        Assert.False(await provider.IsEntitledAsync("anything", "any-tenant", "any-site"));
    }
}
