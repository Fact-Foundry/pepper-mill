namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Decides whether a presented server credential is entitled to a site's pepper. The custody of
/// peppers is PepperMill's job; <em>who is allowed</em> is delegated here — locally against the
/// enrolled tenant credentials, or to an external Platform provider.
/// </summary>
public interface IEntitlementProvider
{
    /// <summary>Whether the credential is valid and entitled to the given tenant's site.</summary>
    Task<bool> IsEntitledAsync(string credential, string tenantId, string siteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local entitlement: resolves the presented credential against the enrolled tenant's stored hash.
/// A credential is entitled to <em>any</em> site under the tenant it was enrolled for, and to no
/// other tenant — so a caller cannot reach another tenant's pepper by changing the request body.
/// </summary>
public sealed class LocalEntitlementProvider : IEntitlementProvider
{
    private readonly ICredentialStore _credentials;

    /// <summary>Creates the provider over the credential store.</summary>
    public LocalEntitlementProvider(ICredentialStore credentials)
    {
        _credentials = credentials;
    }

    /// <inheritdoc />
    public async Task<bool> IsEntitledAsync(string credential, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(credential) || string.IsNullOrWhiteSpace(tenantId))
            return false;

        var record = await _credentials.GetAsync(tenantId, cancellationToken);
        if (record is null)
            return false;

        // The credential must hash to this tenant's stored hash. (siteId is not an entitlement
        // boundary — a tenant credential is good for every site under that tenant.)
        return CredentialHasher.Verify(credential, record.Key2Hash);
    }
}

/// <summary>
/// Hosted-edition entitlement: delegates to fact-foundry-platform. Not yet implemented — the hosted
/// PepperMill will validate the server credential and the site's key-custody subscription via the
/// platform's <c>ILicenseService</c> / <c>ProvisioningKeyService</c> / <c>IMachineActivationService</c>.
/// </summary>
public sealed class PlatformEntitlementProvider : IEntitlementProvider
{
    private readonly ILogger<PlatformEntitlementProvider> _logger;

    /// <summary>Creates the provider.</summary>
    public PlatformEntitlementProvider(ILogger<PlatformEntitlementProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> IsEntitledAsync(string credential, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Platform entitlement mode is configured but not yet implemented; denying request for tenant {TenantId} site {SiteId}.",
            tenantId, siteId);
        return Task.FromResult(false);
    }
}
