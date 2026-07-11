namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A tenant's authentication record, established at enrollment and held at rest. Contains no secret
/// material — only a hash of the credential (<c>key2</c>) plus non-secret provisioning metadata — so
/// a copy of the record cannot be used to authenticate.
/// </summary>
/// <param name="TenantId">The tenant this credential authenticates.</param>
/// <param name="Key2Hash">Lowercase hex SHA-256 of the tenant's bearer credential (<c>key2</c>). The raw credential is never stored.</param>
/// <param name="CallbackUrl">The client callback URL captured at enrollment; pinned and reused for later credential rotations.</param>
/// <param name="RotationIntervalDays">Requested rotation cadence in days; <c>null</c> means the default monthly cadence. Reserved — only the monthly cadence is honored today.</param>
/// <param name="Locked">Whether enrollment is complete; when true, further provision attempts for the tenant are rejected.</param>
/// <param name="CreatedAtUtc">When the credential was enrolled.</param>
public sealed record TenantCredential(
    string TenantId,
    string Key2Hash,
    string CallbackUrl,
    int? RotationIntervalDays,
    bool Locked,
    DateTimeOffset CreatedAtUtc);
