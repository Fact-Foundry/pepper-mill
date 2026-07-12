namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Decides whether a presented server credential is entitled to a site's pepper. The custody of
/// peppers is PepperMill's job; <em>who is allowed</em> is delegated here — locally against the
/// registered site credentials, or to an external Platform provider.
/// </summary>
public interface IEntitlementProvider
{
    /// <summary>Whether the credential is valid and entitled to the given site.</summary>
    Task<bool> IsEntitledAsync(string credential, string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local entitlement: resolves the presented credential against the registered site's stored hash.
/// A credential is scoped to exactly one <c>(tenantId, siteId)</c> — so a leaked credential exposes
/// a single site, and a caller cannot reach another site (or tenant) by changing the request body.
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
    public async Task<bool> IsEntitledAsync(string credential, string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(credential) || string.IsNullOrWhiteSpace(clusterId)
            || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(siteId))
            return false;

        var record = await _credentials.GetAsync(clusterId, tenantId, siteId, cancellationToken);
        if (record is null)
            return false;

        // The credential must hash to this site's stored hash — it is good for this site only.
        return CredentialHasher.Verify(credential, record.Key2Hash);
    }
}

/// <summary>
/// Platform entitlement: delegates the entitlement decision to an external provider. Not yet
/// implemented — it denies every request and logs a warning until wired up.
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
    public Task<bool> IsEntitledAsync(string credential, string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Platform entitlement mode is configured but not yet implemented; denying request for {ClusterId}/{TenantId}/{SiteId}.",
            clusterId, tenantId, siteId);
        return Task.FromResult(false);
    }
}
