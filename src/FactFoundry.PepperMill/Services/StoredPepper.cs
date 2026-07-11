namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A site's current pepper as held at rest (only ever the current epoch — prior epochs are
/// destroyed on rotation, never archived).
/// </summary>
/// <param name="SiteId">The site the pepper belongs to.</param>
/// <param name="Epoch">The epoch id (<c>yyyy-MM</c>) this pepper is valid for.</param>
/// <param name="PepperBase64">The 256-bit pepper, base64-encoded.</param>
/// <param name="CreatedAtUtc">When this pepper was generated.</param>
public sealed record StoredPepper(string SiteId, string Epoch, string PepperBase64, DateTimeOffset CreatedAtUtc);
