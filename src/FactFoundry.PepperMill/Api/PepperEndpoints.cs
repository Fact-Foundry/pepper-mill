using FactFoundry.PepperMill.Services;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Api;

/// <summary>Request for a site's current pepper.</summary>
/// <param name="TenantId">The tenant that owns the site (must match the presented credential).</param>
/// <param name="SiteId">The site whose pepper is requested (must match the presented credential).</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record PepperFetchRequest(string TenantId, string SiteId, string ClusterId = "default");

/// <summary>A site's current pepper and rotation metadata.</summary>
/// <param name="Pepper">The 256-bit pepper, base64-encoded. Hold in memory only; never persist it.</param>
/// <param name="Epoch">The epoch it belongs to (<c>yyyy-MM</c>).</param>
/// <param name="RotatesAtUtc">When it will rotate; re-fetch after this time.</param>
public sealed record PepperFetchResponse(string Pepper, string Epoch, DateTimeOffset RotatesAtUtc);

/// <summary>Registration request: creates a site's pepper and establishes its bearer credential via a callback handshake.</summary>
/// <param name="TenantId">The tenant that owns the site.</param>
/// <param name="SiteId">The site to register (unique within the tenant).</param>
/// <param name="CallbackUrl">The client endpoint PepperMill calls back to obtain <c>key2</c>; pinned for later rotations.</param>
/// <param name="Key1">A per-request nonce the client will verify when PepperMill calls back.</param>
/// <param name="RotationIntervalDays">Optional rotation cadence in days; null means the default monthly cadence.</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record SiteRegistrationRequest(string TenantId, string SiteId, string CallbackUrl, string Key1, int? RotationIntervalDays = null, string ClusterId = "default");

/// <summary>Revoke request: un-registers a site and destroys its pepper.</summary>
/// <param name="TenantId">The tenant that owns the site.</param>
/// <param name="SiteId">The site to revoke.</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record SiteRevokeRequest(string TenantId, string SiteId, string ClusterId = "default");

/// <summary>Force-rotate request: destroys a site's current pepper and issues a fresh one now.</summary>
/// <param name="TenantId">The tenant that owns the site (must match the presented credential).</param>
/// <param name="SiteId">The site whose pepper to rotate.</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record PepperRotateRequest(string TenantId, string SiteId, string ClusterId = "default");

/// <summary>Schedule-update request: changes a site's rotation cadence.</summary>
/// <param name="TenantId">The tenant that owns the site (must match the presented credential).</param>
/// <param name="SiteId">The site to update.</param>
/// <param name="RotationIntervalDays">New cadence in days; null resets to the default monthly cadence.</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record SiteScheduleRequest(string TenantId, string SiteId, int? RotationIntervalDays, string ClusterId = "default");

/// <summary>Credential-rotation request: issues a new <c>key2</c> for a site via the pinned callback URL.</summary>
/// <param name="TenantId">The tenant that owns the site (must match the presented current credential).</param>
/// <param name="SiteId">The site to rotate.</param>
/// <param name="Key1">A fresh per-request nonce the client will verify when PepperMill calls back.</param>
/// <param name="ClusterId">Optional cluster namespace; defaults to <c>"default"</c>.</param>
public sealed record CredentialRotateRequest(string TenantId, string SiteId, string Key1, string ClusterId = "default");

/// <summary>Minimal-API endpoints for pepper custody.</summary>
public static class PepperEndpoints
{
    /// <summary>Maps the pepper and lifecycle endpoints.</summary>
    public static void MapPepperEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/peppers/current", FetchCurrent)
            .WithSummary("Fetch a site's current pepper")
            .WithDescription("Validates the site's bearer credential against the request's cluster/tenant/site, then returns the current-epoch pepper. The caller should hold it in memory only and re-fetch after RotatesAtUtc.");

        v1.MapPost("/webhooks/provision", Register)
            .WithSummary("Register a site (create its pepper, establish its credential)")
            .WithDescription("Establishes a site's bearer credential via a callback handshake — PepperMill calls the supplied callbackUrl with key1, and the client returns key2 — then creates the site's pepper. One-shot: a site that is already registered is rejected.");

        v1.MapPost("/webhooks/revoke", Revoke)
            .WithSummary("Revoke a site (destroy its pepper, un-register)")
            .WithDescription("Destroys the site's pepper and removes its credential, so the site can be registered again. Authorized by the site's current credential.");

        v1.MapPost("/peppers/rotate", ForceRotate)
            .WithSummary("Force-rotate a site's pepper now")
            .WithDescription("Destroys the site's current pepper and issues a fresh one immediately, returning it. Authorized by the site's current credential.");

        v1.MapPost("/tenants/schedule", UpdateSchedule)
            .WithSummary("Update a site's rotation cadence")
            .WithDescription("Stores a new rotationIntervalDays for the site. Authorized by the site's current credential. (Only the monthly cadence is honored today; the value is reserved.)");

        v1.MapPost("/webhooks/rotate-credential", RotateCredential)
            .WithSummary("Rotate a site's credential (key2)")
            .WithDescription("Issues a new key2 via the callback URL captured at registration (never a request-supplied one): PepperMill calls the pinned callbackUrl with key1 and stores the new credential. Authorized by the site's current credential.");
    }

    /// <summary>Normalizes an optional cluster id: null/blank becomes the <c>"default"</c> namespace.</summary>
    private static string Cluster(string? clusterId) => string.IsNullOrWhiteSpace(clusterId) ? "default" : clusterId;

    /// <summary>Returns a site's current pepper after validating the bearer credential against the request's cluster/tenant/site.</summary>
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

        var cluster = Cluster(request.ClusterId);
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);

        if (!await entitlement.IsEntitledAsync(credential, cluster, request.TenantId, request.SiteId, context.RequestAborted))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch.denied", cluster, request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);
        }

        var pepper = await peppers.GetCurrentAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.fetch", cluster, request.TenantId, request.SiteId, pepper.Epoch), context.RequestAborted);

        return Results.Ok(new PepperFetchResponse(pepper.PepperBase64, pepper.Epoch, pepper.RotatesAtUtc));
    }

    /// <summary>Registers a site: guards the callback URL, runs the handshake, stores the credential locked to the site, and creates its pepper.</summary>
    private static async Task<IResult> Register(
        HttpContext context,
        SiteRegistrationRequest request,
        IOptions<PepperMillOptions> options,
        ICredentialStore credentials,
        ICallbackClient callbackClient,
        PepperService peppers,
        IAuditLog audit,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.SiteId))
            return Results.BadRequest(new { error = "siteId is required." });
        if (string.IsNullOrWhiteSpace(request.CallbackUrl))
            return Results.BadRequest(new { error = "callbackUrl is required." });
        if (string.IsNullOrWhiteSpace(request.Key1))
            return Results.BadRequest(new { error = "key1 is required." });

        var cluster = Cluster(request.ClusterId);

        // Guard the callback URL against SSRF before making any outbound request.
        if (!CallbackGuard.IsAllowed(request.CallbackUrl, options.Value.CallbackAllowedHosts, out var reason))
            return Results.Json(new { error = $"callbackUrl is not permitted: {reason}." }, statusCode: 403);

        // One-shot lock: an already-registered site cannot be re-provisioned (only reset then re-registered).
        var existing = await credentials.GetAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        if (existing is { Locked: true })
            return Results.Conflict(new { error = "Site is already registered." });

        var key2 = await callbackClient.RequestCredentialAsync(request.CallbackUrl, cluster, request.TenantId, request.SiteId, request.Key1, context.RequestAborted);
        if (string.IsNullOrEmpty(key2))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.register.failed", cluster, request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Registration callback did not return a credential." }, statusCode: 502);
        }

        var record = new SiteCredential(
            cluster,
            request.TenantId,
            request.SiteId,
            CredentialHasher.Hash(key2),
            request.CallbackUrl,
            request.RotationIntervalDays,
            Locked: true,
            clock.UtcNow);
        await credentials.SaveAsync(record, context.RequestAborted);

        // Create the site's pepper now so it exists as soon as the site is registered.
        await peppers.GetCurrentAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.register", cluster, request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    /// <summary>Revokes a site: destroys its pepper and removes its credential so it can re-register.</summary>
    private static async Task<IResult> Revoke(
        HttpContext context,
        SiteRevokeRequest request,
        IEntitlementProvider entitlement,
        ICredentialStore credentials,
        IPepperStore store,
        IAuditLog audit,
        IClock clock)
    {
        var cluster = Cluster(request.ClusterId);
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SiteId)
            || !await entitlement.IsEntitledAsync(credential, cluster, request.TenantId, request.SiteId, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);

        await store.DeleteAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        await credentials.DeleteAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.revoke", cluster, request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    /// <summary>Force-rotates a site's pepper immediately and returns the fresh one.</summary>
    private static async Task<IResult> ForceRotate(
        HttpContext context,
        PepperRotateRequest request,
        IEntitlementProvider entitlement,
        PepperService peppers,
        IAuditLog audit,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Results.BadRequest(new { error = "tenantId is required." });
        if (string.IsNullOrWhiteSpace(request.SiteId))
            return Results.BadRequest(new { error = "siteId is required." });

        var cluster = Cluster(request.ClusterId);
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);
        if (!await entitlement.IsEntitledAsync(credential, cluster, request.TenantId, request.SiteId, context.RequestAborted))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.rotate.denied", cluster, request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);
        }

        var pepper = await peppers.ForceRotateAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "pepper.rotate", cluster, request.TenantId, request.SiteId, pepper.Epoch), context.RequestAborted);
        return Results.Ok(new PepperFetchResponse(pepper.PepperBase64, pepper.Epoch, pepper.RotatesAtUtc));
    }

    /// <summary>Updates a site's stored rotation cadence (reserved; only the monthly cadence is honored today).</summary>
    private static async Task<IResult> UpdateSchedule(
        HttpContext context,
        SiteScheduleRequest request,
        IEntitlementProvider entitlement,
        ICredentialStore credentials,
        IAuditLog audit,
        IClock clock)
    {
        var cluster = Cluster(request.ClusterId);
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SiteId)
            || !await entitlement.IsEntitledAsync(credential, cluster, request.TenantId, request.SiteId, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);

        var record = await credentials.GetAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        if (record is null)
            return Results.Json(new { error = "Site is not registered." }, statusCode: 403);

        await credentials.SaveAsync(record with { RotationIntervalDays = request.RotationIntervalDays }, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.schedule.update", cluster, request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    /// <summary>Issues a new <c>key2</c> for a site via the callback URL pinned at registration (never a request-supplied one).</summary>
    private static async Task<IResult> RotateCredential(
        HttpContext context,
        CredentialRotateRequest request,
        IEntitlementProvider entitlement,
        ICredentialStore credentials,
        ICallbackClient callbackClient,
        IAuditLog audit,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(request.Key1))
            return Results.BadRequest(new { error = "key1 is required." });

        var cluster = Cluster(request.ClusterId);
        var credential = GetBearerCredential(context);
        if (credential is null)
            return Results.Json(new { error = "Missing bearer credential." }, statusCode: 401);
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SiteId)
            || !await entitlement.IsEntitledAsync(credential, cluster, request.TenantId, request.SiteId, context.RequestAborted))
            return Results.Json(new { error = "Not entitled for this site." }, statusCode: 403);

        var record = await credentials.GetAsync(cluster, request.TenantId, request.SiteId, context.RequestAborted);
        if (record is null)
            return Results.Json(new { error = "Site is not registered." }, statusCode: 403);

        // Call back to the URL pinned at registration — never a request-supplied one — so an update
        // cannot redirect the handshake to a rogue endpoint.
        var newKey2 = await callbackClient.RequestCredentialAsync(record.CallbackUrl, cluster, request.TenantId, request.SiteId, request.Key1, context.RequestAborted);
        if (string.IsNullOrEmpty(newKey2))
        {
            await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.credential.rotate.failed", cluster, request.TenantId, request.SiteId), context.RequestAborted);
            return Results.Json(new { error = "Rotation callback did not return a credential." }, statusCode: 502);
        }

        await credentials.SaveAsync(record with { Key2Hash = CredentialHasher.Hash(newKey2) }, context.RequestAborted);
        await audit.RecordAsync(new AuditEntry(clock.UtcNow, "site.credential.rotate", cluster, request.TenantId, request.SiteId), context.RequestAborted);
        return Results.Ok();
    }

    /// <summary>Extracts the bearer token from the <c>Authorization</c> header, or null if it is absent or malformed.</summary>
    private static string? GetBearerCredential(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
