using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FactFoundry.PepperMill.Api;
using FactFoundry.PepperMill.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactFoundry.PepperMill.Tests;

/// <summary>End-to-end tests driving the real pepper endpoints via the hosted app.</summary>
public class PepperEndpointsTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private const string CallbackUrl = "https://callback.test/hook";
    private const string EnrolledKey2 = "enrolled-key2-abcdef";
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pm-api-" + Guid.NewGuid());

    /// <summary>Stands in for the client callback endpoint — always issues the same <c>key2</c>.</summary>
    private sealed class FakeCallbackClient : ICallbackClient
    {
        public Task<string?> RequestCredentialAsync(string callbackUrl, string tenantId, string key1, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(EnrolledKey2);
    }

    private sealed class Factory(string storeDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); // ephemeral storage key, no config file needed
            builder.UseSetting("PepperMill:EntitlementMode", "Local");
            builder.UseSetting("PepperMill:StorePath", storeDir);
            builder.UseSetting("PepperMill:CallbackAllowedHosts:0", "callback.test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICallbackClient>();
                services.AddSingleton<ICallbackClient, FakeCallbackClient>();
            });
        }
    }

    private HttpClient Client() => new Factory(_storeDir).CreateClient();

    private static HttpRequestMessage Enroll(string tenantId, string callbackUrl = CallbackUrl, string key1 = "k1")
        => new(HttpMethod.Post, "/v1/webhooks/provision") { Content = JsonContent.Create(new { tenantId, callbackUrl, key1 }) };

    private static HttpRequestMessage Fetch(string siteId, string? credential, string tenantId = Tenant)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/current")
        {
            Content = JsonContent.Create(new { tenantId, siteId }),
        };
        if (credential is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return req;
    }

    private static async Task EnrollTenant(HttpClient client, string tenantId = Tenant)
    {
        var resp = await client.SendAsync(Enroll(tenantId));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Enroll_ThenFetch_ReturnsA256BitPepper()
    {
        var client = Client();
        await EnrollTenant(client);

        var resp = await client.SendAsync(Fetch("site-1", EnrolledKey2));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PepperFetchResponse>();
        Assert.NotNull(body);
        Assert.Equal($"{DateTimeOffset.UtcNow:yyyy-MM}", body!.Epoch);
        Assert.Equal(32, Convert.FromBase64String(body.Pepper).Length);
    }

    [Fact]
    public async Task Fetch_IsStableWithinTheEpoch()
    {
        var client = Client();
        await EnrollTenant(client);

        var first = await (await client.SendAsync(Fetch("site-1", EnrolledKey2))).Content.ReadFromJsonAsync<PepperFetchResponse>();
        var second = await (await client.SendAsync(Fetch("site-1", EnrolledKey2))).Content.ReadFromJsonAsync<PepperFetchResponse>();

        Assert.Equal(first!.Pepper, second!.Pepper);
    }

    [Fact]
    public async Task Fetch_MissingCredential_Returns401()
    {
        var client = Client();
        await EnrollTenant(client);

        var resp = await client.SendAsync(Fetch("site-1", credential: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_WrongCredential_Returns403()
    {
        var client = Client();
        await EnrollTenant(client);

        var resp = await client.SendAsync(Fetch("site-1", credential: "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_UnenrolledTenant_Returns403()
    {
        var resp = await Client().SendAsync(Fetch("site-1", EnrolledKey2, tenantId: "not-enrolled"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_MissingTenant_Returns400()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/current")
        {
            Content = JsonContent.Create(new { siteId = "site-1" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EnrolledKey2);

        var resp = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Enroll_Twice_Returns409()
    {
        var client = Client();
        await EnrollTenant(client);

        var second = await client.SendAsync(Enroll(Tenant));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Enroll_DisallowedCallbackHost_Returns403()
    {
        var resp = await Client().SendAsync(Enroll(Tenant, callbackUrl: "https://evil.example/hook"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var resp = await Client().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }
}
