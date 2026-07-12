namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A site's authentication record, established when the site is registered and held at rest. Contains
/// no secret material — only a hash of the credential (<c>key2</c>) plus non-secret provisioning
/// metadata — so a copy of the record cannot be used to authenticate. Keyed by
/// <c>(TenantId, SiteId)</c>, so a leaked credential is scoped to a single site.
/// </summary>
/// <param name="TenantId">The tenant that owns the site.</param>
/// <param name="SiteId">The site this credential authenticates (unique within the tenant).</param>
/// <param name="Key2Hash">Lowercase hex SHA-256 of the site's bearer credential (<c>key2</c>). The raw credential is never stored.</param>
/// <param name="CallbackUrl">The client callback URL captured at registration; pinned and reused for later credential rotations.</param>
/// <param name="RotationIntervalDays">Requested rotation cadence in days; <c>null</c> means the default monthly cadence. Reserved — only the monthly cadence is honored today.</param>
/// <param name="Locked">Whether registration is complete; when true, further registration attempts for the site are rejected.</param>
/// <param name="CreatedAtUtc">When the credential was registered.</param>
public sealed record SiteCredential(
    string TenantId,
    string SiteId,
    string Key2Hash,
    string CallbackUrl,
    int? RotationIntervalDays,
    bool Locked,
    DateTimeOffset CreatedAtUtc);
