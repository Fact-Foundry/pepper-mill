namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Persists per-site authentication records (<see cref="SiteCredential"/>). Holds only credential
/// hashes and non-secret provisioning metadata — never raw credentials or peppers. One record per
/// site, keyed by the composite <c>(clusterId, tenantId, siteId)</c>.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the credential record for a site, or null if the site is not registered.</summary>
    Task<SiteCredential?> GetAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default);

    /// <summary>Stores (creating or replacing) a site's credential record.</summary>
    Task SaveAsync(SiteCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Removes a site's credential record entirely (e.g. to allow re-registration after a reset).</summary>
    Task DeleteAsync(string clusterId, string tenantId, string siteId, CancellationToken cancellationToken = default);
}
