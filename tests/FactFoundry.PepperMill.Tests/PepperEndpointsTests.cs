using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FactFoundry.PepperMill.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactFoundry.PepperMill.Tests;

/// <summary>End-to-end tests driving the real pepper endpoints via the hosted app.</summary>
public class PepperEndpointsTests : IDisposable
{
    private const string Credential = "test-cred";
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pm-api-" + Guid.NewGuid());

    private sealed class Factory(string storeDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); // ephemeral storage key, no config file needed
            builder.UseSetting("PepperMill:EntitlementMode", "Local");
            builder.UseSetting("PepperMill:LocalServerCredential", Credential);
            builder.UseSetting("PepperMill:StorePath", storeDir);
        }
    }

    private HttpClient Client() => new Factory(_storeDir).CreateClient();

    private const string Tenant = "tenant-1";

    private static HttpRequestMessage Fetch(string siteId, string? credential, string? tenantId = Tenant)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/current")
        {
            Content = JsonContent.Create(new { tenantId, siteId }),
        };
        if (credential is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return req;
    }

    [Fact]
    public async Task Fetch_MissingCredential_Returns401()
    {
        var resp = await Client().SendAsync(Fetch("site-1", credential: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_MissingTenant_Returns400()
    {
        var resp = await Client().SendAsync(Fetch("site-1", Credential, tenantId: null));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_WrongCredential_Returns403()
    {
        var resp = await Client().SendAsync(Fetch("site-1", credential: "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_ValidCredential_ReturnsA256BitPepper()
    {
        var resp = await Client().SendAsync(Fetch("site-1", Credential));

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
        var first = await (await client.SendAsync(Fetch("site-1", Credential))).Content.ReadFromJsonAsync<PepperFetchResponse>();
        var second = await (await client.SendAsync(Fetch("site-1", Credential))).Content.ReadFromJsonAsync<PepperFetchResponse>();

        Assert.Equal(first!.Pepper, second!.Pepper);
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
