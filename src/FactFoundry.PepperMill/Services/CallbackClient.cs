using System.Net.Http.Json;

namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Performs the registration callback: PepperMill calls the client's <c>callbackUrl</c> with the
/// registration challenge and receives the credential (<c>key2</c>) the client issues for the site.
/// </summary>
public interface ICallbackClient
{
    /// <summary>
    /// Calls <paramref name="callbackUrl"/> with <c>{ tenantId, key1 }</c> and returns the <c>key2</c>
    /// the client responds with, or null if the callback failed or returned no credential.
    /// </summary>
    Task<string?> RequestCredentialAsync(string callbackUrl, string tenantId, string key1, CancellationToken cancellationToken = default);
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
    public async Task<string?> RequestCredentialAsync(string callbackUrl, string tenantId, string key1, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(callbackUrl, new { tenantId, key1 }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Registration callback for tenant {TenantId} returned HTTP {Status}.", tenantId, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<CallbackResponse>(cancellationToken);
            if (string.IsNullOrEmpty(body?.Key2))
            {
                _logger.LogWarning("Registration callback for tenant {TenantId} returned no key2.", tenantId);
                return null;
            }

            return body.Key2;
        }
        catch (Exception ex)
        {
            // Never let a callback failure surface the URL/exception detail to the caller; log and deny.
            _logger.LogError(ex, "Registration callback for tenant {TenantId} failed.", tenantId);
            return null;
        }
    }

    private sealed record CallbackResponse(string Key2);
}
