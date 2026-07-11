namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Persists per-tenant authentication records (<see cref="TenantCredential"/>). Holds only credential
/// hashes and non-secret provisioning metadata — never raw credentials or peppers. One record per
/// tenant, keyed by tenant id.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the credential record for a tenant, or null if the tenant is not enrolled.</summary>
    Task<TenantCredential?> GetAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Stores (creating or replacing) a tenant's credential record.</summary>
    Task SaveAsync(TenantCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Removes a tenant's credential record entirely (e.g. to allow re-enrollment after a reset).</summary>
    Task DeleteAsync(string tenantId, CancellationToken cancellationToken = default);
}
