using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Decides whether a presented server credential is entitled to a site's pepper. The custody of
/// peppers is PepperMill's job; <em>who is allowed</em> is delegated here — locally in the OSS
/// edition, or to fact-foundry-platform in the hosted edition.
/// </summary>
public interface IEntitlementProvider
{
    /// <summary>Whether the credential is valid and entitled to the given site.</summary>
    Task<bool> IsEntitledAsync(string credential, string siteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// OSS-edition entitlement: a single shared server credential from configuration. A valid
/// credential is entitled to any site (the operator runs both PepperMill and the servers).
/// </summary>
public sealed class LocalEntitlementProvider : IEntitlementProvider
{
    private readonly string? _configuredCredential;

    /// <summary>Creates the provider from options.</summary>
    public LocalEntitlementProvider(IOptions<PepperMillOptions> options)
    {
        _configuredCredential = options.Value.LocalServerCredential;
    }

    /// <inheritdoc />
    public Task<bool> IsEntitledAsync(string credential, string siteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_configuredCredential) || string.IsNullOrEmpty(credential))
            return Task.FromResult(false);

        // Compare fixed-length hashes so the check is constant-time regardless of input length.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_configuredCredential));
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(presented, expected));
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
    public Task<bool> IsEntitledAsync(string credential, string siteId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Platform entitlement mode is configured but not yet implemented; denying request for site {SiteId}.",
            siteId);
        return Task.FromResult(false);
    }
}
