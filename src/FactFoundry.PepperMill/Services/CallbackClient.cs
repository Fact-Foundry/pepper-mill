using System.Net.Http.Json;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Performs the registration callback: PepperMill calls the client's <c>callbackUrl</c> with the
/// registration challenge and receives the credential (<c>key2</c>) the client issues for the site.
/// </summary>
public interface ICallbackClient
{
    /// <summary>
    /// Calls <paramref name="callbackUrl"/> with <c>{ clusterId, tenantId, siteId, key1 }</c> and returns
    /// the <c>key2</c> the client responds with, or null if the callback failed or returned no credential.
    /// The client correlates by <c>key1</c>; the identity fields let it verify what it is issuing for.
    /// </summary>
    Task<string?> RequestCredentialAsync(string callbackUrl, string clusterId, string tenantId, string siteId, string key1, CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation of <see cref="ICallbackClient"/>. The caller must guard the URL first.</summary>
public sealed class HttpCallbackClient : ICallbackClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpCallbackClient> _logger;

    /// <summary>Creates the client over an injected <see cref="HttpClient"/>.</summary>
    public HttpCallbackClient(HttpClient http, ILogger<HttpCallbackClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> RequestCredentialAsync(string callbackUrl, string clusterId, string tenantId, string siteId, string key1, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(callbackUrl, new { clusterId, tenantId, siteId, key1 }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Registration callback for {ClusterId}/{TenantId}/{SiteId} returned HTTP {Status}.", clusterId, tenantId, siteId, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<CallbackResponse>(cancellationToken);
            if (string.IsNullOrEmpty(body?.Key2))
            {
                _logger.LogWarning("Registration callback for {ClusterId}/{TenantId}/{SiteId} returned no key2.", clusterId, tenantId, siteId);
                return null;
            }

            return body.Key2;
        }
        catch (Exception ex)
        {
            // Never let a callback failure surface the URL/exception detail to the caller; log and deny.
            _logger.LogError(ex, "Registration callback for {ClusterId}/{TenantId}/{SiteId} failed.", clusterId, tenantId, siteId);
            return null;
        }
    }

    private sealed record CallbackResponse(string Key2);
}
