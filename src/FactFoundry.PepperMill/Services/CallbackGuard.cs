namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Guards the registration callback against SSRF. PepperMill only ever calls back to a URL whose host
/// is on the operator-configured allowlist; anything else is refused before any outbound request is made.
/// </summary>
public static class CallbackGuard
{
    /// <summary>
    /// Whether <paramref name="callbackUrl"/> is a well-formed http/https URL whose host is allowlisted.
    /// Sets <paramref name="reason"/> to a short human-readable cause when it is not allowed.
    /// </summary>
    public static bool IsAllowed(string callbackUrl, IReadOnlyCollection<string> allowedHosts, out string reason)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "scheme must be http or https";
            return false;
        }

        if (allowedHosts is null || allowedHosts.Count == 0)
        {
            reason = "no callback hosts are allowlisted";
            return false;
        }

        if (!allowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"host '{uri.Host}' is not allowlisted";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
