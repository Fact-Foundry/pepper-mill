namespace FactFoundry.PepperMill;

/// <summary>
/// Configuration for PepperMill, bound from the <c>PepperMill</c> configuration section.
/// </summary>
public sealed class PepperMillOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "PepperMill";

    /// <summary>
    /// How requests are authorized: <c>Local</c> (resolve the presented credential against the enrolled
    /// tenant records) or <c>Platform</c> (delegate entitlement to an external provider — not implemented).
    /// </summary>
    public string EntitlementMode { get; set; } = "Local";

    /// <summary>
    /// Base64 of the 32-byte AES-GCM master key used to encrypt peppers at rest. Required in
    /// Production. In Development an ephemeral key is generated (peppers will not survive a restart).
    /// </summary>
    public string? StorageKeyBase64 { get; set; }

    /// <summary>
    /// Hostnames PepperMill is permitted to call back to during tenant enrollment. The enrollment
    /// handshake makes an outbound request to the client-supplied <c>callbackUrl</c>; only URLs whose
    /// host appears here are contacted, so the endpoint cannot be abused as an SSRF primitive. Empty
    /// means no callback is allowed (enrollment is refused until the operator configures this).
    /// </summary>
    public List<string> CallbackAllowedHosts { get; set; } = [];

    /// <summary>Directory holding the encrypted per-site pepper files and per-tenant credential records.</summary>
    public string StorePath { get; set; } = "peppers";
}
