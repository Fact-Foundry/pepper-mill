using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Api;

/// <summary>Request for a site's current pepper.</summary>
/// <param name="TenantId">The tenant that owns the site.</param>
/// <param name="SiteId">The site whose pepper is requested (unique within the tenant).</param>
public sealed record PepperFetchRequest(string TenantId, string SiteId);

/// <summary>A site's current pepper and rotation metadata.</summary>
/// <param name="Pepper">The 256-bit pepper, base64-encoded. Hold in memory only; never persist it.</param>
/// <param name="Epoch">The epoch it belongs to (<c>yyyy-MM</c>).</param>
/// <param name="RotatesAtUtc">When it will rotate; re-fetch after this time.</param>
public sealed record PepperFetchResponse(string Pepper, string Epoch, DateTimeOffset RotatesAtUtc);

/// <summary>A platform lifecycle notification for a single site.</summary>
/// <param name="TenantId">The tenant that owns the affected site.</param>
/// <param name="SiteId">The affected site (unique within the tenant).</param>
public sealed record SiteLifecycleRequest(string TenantId, string SiteId);

/// <summary>Minimal-API endpoints for pepper custody.</summary>
public static class PepperEndpoints
{
    /// <summary>Maps the pepper and lifecycle endpoints.</summary>
    public static void MapPepperEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/peppers/current", FetchCurrent)
            .WithSummary("Fetch a site's current pepper")
            .WithDescription("Validates the bearer server credential and the site's entitlement, then returns the current-epoch pepper. The caller should hold it in memory only and re-fetch after RotatesAtUtc.");

        v1.MapPost("/webhooks/provision", Provision)
            .WithSummary("Provision a site's pepper (platform lifecycle)")
            .WithDescription("Ensures a pepper exists for the site — e.g. when a key-custody subscription begins.");

        v1.MapPost("/webhooks/revoke", Revoke)
            .WithSummary("Revoke a site's pepper (platform lifecycle)")
            .WithDescription("Destroys the site's pepper — e.g. when a subscription is cancelled.");
    }

    private static async Task<IResult> FetchCurrent(
        HttpContext context,
        PepperFetchRequest request,
        IEntitlementProvider entitlement,
        PepperService peppers,
        IAuditLog audit,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.SiteId))
            return Results.BadRequest(new { error = "siteId is required." });

        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer server credential." }, statusCode: 401);

        if (!await entitlement.IsEntitledAsync(credential, request.TenantId, request.SiteId, context.RequestAborted))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch.denied", request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);
        }

        var pepper = await peppers.GetCurrentAsync(request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch", request.TenantId, request.SiteId, pepper.Epoch), context.RequestAborted);

        return Results.Ok(new PepperFetchResponse(pepper.PepperBase64, pepper.Epoch, pepper.RotatesAtUtc));
    }

    private static async Task<IResult> Provision(
        HttpContext context,
        SiteLifecycleRequest request,
        IEntitlementProvider entitlement,
        PepperService peppers,
        IAuditLog audit,
        IClock clock)
    {
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer server credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SiteId)
            || !await entitlement.IsEntitledAsync(credential, request.TenantId, request.SiteId, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);

        await peppers.GetCurrentAsync(request.TenantId, request.SiteId, context.RequestAborted); // creates if absent
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.provision", request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    private static async Task<IResult> Revoke(
        HttpContext context,
        SiteLifecycleRequest request,
        IEntitlementProvider entitlement,
        IPepperStore store,
        IAuditLog audit,
        IClock clock)
    {
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer server credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SiteId)
            || !await entitlement.IsEntitledAsync(credential, request.TenantId, request.SiteId, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);

        await store.DeleteAsync(request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.revoke", request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    private static string? GetBearerCredential(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
