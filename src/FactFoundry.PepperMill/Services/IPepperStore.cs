namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Custody store for per-site peppers. Holds only the current epoch's pepper for each site;
/// rotation overwrites it, which destroys the prior value (never archived). Implementations must
/// keep peppers encrypted at rest and must never log them. A pepper is keyed by the composite
/// (<c>tenantId</c>, <c>siteId</c>): a site id is unique only within its tenant.
/// </summary>
public interface IPepperStore
{
    /// <summary>Returns the stored pepper for a tenant's site, or null if none exists yet.</summary>
    Task<StoredPepper?> GetAsync(string tenantId, string siteId, CancellationToken cancellationToken = default);

    /// <summary>Stores (and, if one already existed, irreversibly replaces) a site's pepper.</summary>
    Task SaveAsync(StoredPepper pepper, CancellationToken cancellationToken = default);

    /// <summary>Removes a tenant's site pepper entirely (e.g. on subscription revocation).</summary>
    Task DeleteAsync(string tenantId, string siteId, CancellationToken cancellationToken = default);

    /// <summary>Lists all currently stored peppers — used by the rotation worker.</summary>
    Task<IReadOnlyList<StoredPepper>> ListAsync(CancellationToken cancellationToken = default);
}
