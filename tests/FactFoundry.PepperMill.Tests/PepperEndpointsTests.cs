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

/// <summary>End-to-end tests driving the real pepper endpoints via the running app.</summary>
public class PepperEndpointsTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private const string CallbackUrl = "https://callback.test/hook";
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pm-api-" + Guid.NewGuid());

    // The stand-in client issues a key2 derived from key1; registration uses a key1 derived from the
    // site id, so each site ends up with its own distinct key2.
    private static string Key1For(string siteId) => $"k1-{siteId}";
    private static string Key2For(string key1) => $"k2::{key1}";
    private static string SiteKey2(string siteId) => Key2For(Key1For(siteId));

    /// <summary>Stands in for the client callback endpoint — issues a <c>key2</c> derived from the challenge <c>key1</c>.</summary>
    private sealed class FakeCallbackClient : ICallbackClient
    {
        public Task<string?> RequestCredentialAsync(string callbackUrl, string tenantId, string key1, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(Key2For(key1));
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

    private static HttpRequestMessage Register(string siteId, string tenantId = Tenant, string callbackUrl = CallbackUrl, string? key1 = null)
        => new(HttpMethod.Post, "/v1/webhooks/provision")
        {
            Content = JsonContent.Create(new { tenantId, siteId, callbackUrl, key1 = key1 ?? Key1For(siteId) }),
        };

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

    private static async Task RegisterSite(HttpClient client, string siteId = "site-1", string tenantId = Tenant)
    {
        var resp = await client.SendAsync(Register(siteId, tenantId));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Register_ThenFetch_ReturnsA256BitPepper()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var resp = await client.SendAsync(Fetch("site-1", SiteKey2("site-1")));

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
        await RegisterSite(client, "site-1");

        var first = await (await client.SendAsync(Fetch("site-1", SiteKey2("site-1")))).Content.ReadFromJsonAsync<PepperFetchResponse>();
        var second = await (await client.SendAsync(Fetch("site-1", SiteKey2("site-1")))).Content.ReadFromJsonAsync<PepperFetchResponse>();

        Assert.Equal(first!.Pepper, second!.Pepper);
    }

    [Fact]
    public async Task Fetch_MissingCredential_Returns401()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var resp = await client.SendAsync(Fetch("site-1", credential: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_WrongCredential_Returns403()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var resp = await client.SendAsync(Fetch("site-1", credential: "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_UnregisteredSite_Returns403()
    {
        // Sites are no longer implicit — a fetch for a site that never registered is denied.
        var resp = await Client().SendAsync(Fetch("never-registered", SiteKey2("never-registered")));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Fetch_MissingTenant_Returns400()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/current")
        {
            Content = JsonContent.Create(new { siteId = "site-1" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SiteKey2("site-1"));

        var resp = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CredentialForOneSite_CannotFetchAnother_Returns403()
    {
        var client = Client();
        await RegisterSite(client, "blog");
        await RegisterSite(client, "shop");

        // blog's key2 works for blog...
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Fetch("blog", SiteKey2("blog")))).StatusCode);
        // ...but not for shop — a leaked credential is scoped to a single site.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Fetch("shop", SiteKey2("blog")))).StatusCode);
    }

    [Fact]
    public async Task Register_Twice_Returns409()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var second = await client.SendAsync(Register("site-1"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_DisallowedCallbackHost_Returns403()
    {
        var resp = await Client().SendAsync(Register("site-1", callbackUrl: "https://evil.example/hook"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Revoke_UnregistersTheSite()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var revoke = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/revoke")
        {
            Content = JsonContent.Create(new { tenantId = Tenant, siteId = "site-1" }),
        };
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SiteKey2("site-1"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(revoke)).StatusCode);

        // After revoke the site is unregistered — a fetch is denied.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Fetch("site-1", SiteKey2("site-1")))).StatusCode);
    }

    [Fact]
    public async Task ForceRotate_IssuesADifferentPepper()
    {
        var client = Client();
        await RegisterSite(client, "site-1");
        var before = await (await client.SendAsync(Fetch("site-1", SiteKey2("site-1")))).Content.ReadFromJsonAsync<PepperFetchResponse>();

        var rotate = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/rotate")
        {
            Content = JsonContent.Create(new { tenantId = Tenant, siteId = "site-1" }),
        };
        rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SiteKey2("site-1"));
        var rotateResp = await client.SendAsync(rotate);

        Assert.Equal(HttpStatusCode.OK, rotateResp.StatusCode);
        var rotated = await rotateResp.Content.ReadFromJsonAsync<PepperFetchResponse>();
        Assert.NotEqual(before!.Pepper, rotated!.Pepper);

        var after = await (await client.SendAsync(Fetch("site-1", SiteKey2("site-1")))).Content.ReadFromJsonAsync<PepperFetchResponse>();
        Assert.Equal(rotated.Pepper, after!.Pepper);
    }

    [Fact]
    public async Task ForceRotate_WrongCredential_Returns403()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var rotate = new HttpRequestMessage(HttpMethod.Post, "/v1/peppers/rotate")
        {
            Content = JsonContent.Create(new { tenantId = Tenant, siteId = "site-1" }),
        };
        rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rotate)).StatusCode);
    }

    [Fact]
    public async Task RotateCredential_RetiresOldKeyAndActivatesNew()
    {
        var client = Client();
        await RegisterSite(client, "site-1");
        var oldKey2 = SiteKey2("site-1");

        var rotate = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/rotate-credential")
        {
            Content = JsonContent.Create(new { tenantId = Tenant, siteId = "site-1", key1 = "k1-rot" }),
        };
        rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldKey2);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(rotate)).StatusCode);

        // The pinned callback issued key2 for the new key1; the old credential is retired.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Fetch("site-1", oldKey2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Fetch("site-1", Key2For("k1-rot")))).StatusCode);
    }

    [Fact]
    public async Task UpdateSchedule_WithValidCredential_Returns200()
    {
        var client = Client();
        await RegisterSite(client, "site-1");

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/tenants/schedule")
        {
            Content = JsonContent.Create(new { tenantId = Tenant, siteId = "site-1", rotationIntervalDays = 90 }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SiteKey2("site-1"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
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
