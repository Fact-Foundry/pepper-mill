namespace FactFoundry.PepperMill;

/// <summary>
/// Configuration for PepperMill, bound from the <c>PepperMill</c> configuration section.
/// </summary>
public sealed class PepperMillOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "PepperMill";

    /// <summary>
    /// How requests are authorized: <c>Local</c> (a shared server credential from config) or
    /// <c>Platform</c> (delegate entitlement to fact-foundry-platform — hosted edition).
    /// </summary>
    public string EntitlementMode { get; set; } = "Local";

    /// <summary>
    /// Base64 of the 32-byte AES-GCM master key used to encrypt peppers at rest. Required in
    /// Production. In Development an ephemeral key is generated (peppers will not survive a restart).
    /// </summary>
    public string? StorageKeyBase64 { get; set; }

    /// <summary>
    /// The shared server credential accepted in <c>Local</c> mode. A TelemetryForge server presents
    /// it as a bearer token. Required in Local mode.
    /// </summary>
    public string? LocalServerCredential { get; set; }

    /// <summary>Directory holding the encrypted per-site pepper files.</summary>
    public string StorePath { get; set; } = "peppers";
}
