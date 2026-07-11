using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Api;

/// <summary>Request for a site's current pepper.</summary>
/// <param name="TenantId">The tenant that owns the site (must match the presented credential).</param>
/// <param name="SiteId">The site whose pepper is requested (unique within the tenant).</param>
public sealed record PepperFetchRequest(string TenantId, string SiteId);

/// <summary>A site's current pepper and rotation metadata.</summary>
/// <param name="Pepper">The 256-bit pepper, base64-encoded. Hold in memory only; never persist it.</param>
/// <param name="Epoch">The epoch it belongs to (<c>yyyy-MM</c>).</param>
/// <param name="RotatesAtUtc">When it will rotate; re-fetch after this time.</param>
public sealed record PepperFetchResponse(string Pepper, string Epoch, DateTimeOffset RotatesAtUtc);

/// <summary>Enrollment request: establishes a tenant's bearer credential via a callback handshake.</summary>
/// <param name="TenantId">The tenant to enroll.</param>
/// <param name="CallbackUrl">The client endpoint PepperMill calls back to obtain <c>key2</c>; pinned for later rotations.</param>
/// <param name="Key1">A per-request nonce the client will verify when PepperMill calls back.</param>
/// <param name="RotationIntervalDays">Optional rotation cadence in days; null means the default monthly cadence.</param>
public sealed record TenantEnrollmentRequest(string TenantId, string CallbackUrl, string Key1, int? RotationIntervalDays = null);

/// <summary>Revoke request: un-enrolls a tenant and destroys its peppers.</summary>
/// <param name="TenantId">The tenant to revoke.</param>
public sealed record TenantRevokeRequest(string TenantId);

/// <summary>Minimal-API endpoints for pepper custody.</summary>
public static class PepperEndpoints
{
    /// <summary>Maps the pepper and lifecycle endpoints.</summary>
    public static void MapPepperEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/peppers/current", FetchCurrent)
            .WithSummary("Fetch a site's current pepper")
            .WithDescription("Validates the tenant's bearer credential against the request's tenantId, then returns the current-epoch pepper for the site. The caller should hold it in memory only and re-fetch after RotatesAtUtc.");

        v1.MapPost("/webhooks/provision", Enroll)
            .WithSummary("Enroll a tenant (establish its credential)")
            .WithDescription("Establishes a tenant's bearer credential via a callback handshake: PepperMill calls the supplied callbackUrl with key1, and the client returns key2. One-shot — a tenant that is already enrolled is rejected.");

        v1.MapPost("/webhooks/revoke", Revoke)
            .WithSummary("Revoke a tenant (destroy its peppers, un-enroll)")
            .WithDescription("Destroys all of the tenant's peppers and removes its credential, so the tenant can be enrolled again. Authorized by the tenant's current credential.");
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
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);

        if (!await entitlement.IsEntitledAsync(credential, request.TenantId, request.SiteId, context.RequestAborted))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch.denied", request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);
        }

        var pepper = await peppers.GetCurrentAsync(request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch", request.TenantId, request.SiteId, pepper.Epoch), context.RequestAborted);

        return Results.Ok(new PepperFetchResponse(pepper.PepperBase64, pepper.Epoch, pepper.RotatesAtUtc));
    }

    private static async Task<IResult> Enroll(
        HttpContext context,
        TenantEnrollmentRequest request,
        IOptions<PepperMillOptions> options,
        ICredentialStore credentials,
        ICallbackClient callbackClient,
        IAuditLog audit,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.CallbackUrl))
            return Results.BadRequest(new { error = "callbackUrl is required." });
        if (string.IsNullOrWhiteSpace(request.Key1))
            return Results.BadRequest(new { error = "key1 is required." });

        // Guard the callback URL against SSRF before making any outbound request.
        if (!CallbackGuard.IsAllowed(request.CallbackUrl, options.Value.CallbackAllowedHosts, out var reason))
            return Results.Json(new { error = $"callbackUrl is not permitted: {reason}." }, statusCode: 403);

        // One-shot lock: an already-enrolled tenant cannot be re-provisioned (only reset then re-enrolled).
        var existing = await credentials.GetAsync(request.TenantId, context.RequestAborted);
        if (existing is { Locked: true })
            return Results.Conflict(new { error = "Tenant is already enrolled." });

        var key2 = await callbackClient.RequestCredentialAsync(request.CallbackUrl, request.TenantId, request.Key1, context.RequestAborted);
        if (string.IsNullOrEmpty(key2))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "tenant.enroll.failed", request.TenantId), context.RequestAborted);
            return Results.Json(new { error = "Enrollment callback did not return a credential." }, statusCode: 502);
        }

        var record = new TenantCredential(
            request.TenantId,
            CredentialHasher.Hash(key2),
            request.CallbackUrl,
            request.RotationIntervalDays,
            Locked: true,
            clock.UtcNow);
        await credentials.SaveAsync(record, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "tenant.enroll", request.TenantId), context.RequestAborted);
        return Results.Ok();
    }

    private static async Task<IResult> Revoke(
        HttpContext context,
        TenantRevokeRequest request,
        IEntitlementProvider entitlement,
        ICredentialStore credentials,
        IPepperStore store,
        IAuditLog audit,
        IClock clock)
    {
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId)
            || !await entitlement.IsEntitledAsync(credential, request.TenantId, siteId: string.Empty, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this tenant." }, statusCode: 403);

        // Destroy every pepper belonging to the tenant, then remove the credential so it can re-enroll.
        foreach (var pepper in await store.ListAsync(context.RequestAborted))
        {
            if (pepper.TenantId == request.TenantId)
                await store.DeleteAsync(pepper.TenantId, pepper.SiteId, context.RequestAborted);
        }
        await credentials.DeleteAsync(request.TenantId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "tenant.revoke", request.TenantId), context.RequestAborted);
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
