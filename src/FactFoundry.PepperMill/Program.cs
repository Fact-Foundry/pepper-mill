using System.Security.Cryptography;
using FactFoundry.PepperMill;
using FactFoundry.PepperMill.Api;
using FactFoundry.PepperMill.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PepperMillOptions>(builder.Configuration.GetSection(PepperMillOptions.SectionName));
var options = builder.Configuration.GetSection(PepperMillOptions.SectionName).Get<PepperMillOptions>() ?? new PepperMillOptions();

// OpenAPI document (served at /openapi/v1.json) + Scalar interactive reference (at /scalar/v1).
builder.Services.AddOpenApi();

builder.Services.AddSingleton<IClock, SystemClock>();

// Encrypted pepper store — resolve the 32-byte master key up front.
var (masterKey, ephemeralKey) = ResolveMasterKey(options, builder.Environment);
builder.Services.AddSingleton<IPepperStore>(sp => new EncryptedFilePepperStore(
    masterKey, options.StorePath, sp.GetRequiredService<ILogger<EncryptedFilePepperStore>>()));

builder.Services.AddSingleton<PepperService>();
builder.Services.AddSingleton<IAuditLog, FileAuditLog>();

// Per-site credential records (established at registration; hashes only).
builder.Services.AddSingleton<ICredentialStore, FileCredentialStore>();

// Outbound client for the registration callback handshake.
builder.Services.AddHttpClient<ICallbackClient, HttpCallbackClient>();

// Entitlement provider by mode: Local (registered credentials) or Platform (external delegation).
if (string.Equals(options.EntitlementMode, "Platform", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEntitlementProvider, PlatformEntitlementProvider>();
else
    builder.Services.AddSingleton<IEntitlementProvider, LocalEntitlementProvider>();

builder.Services.AddHostedService<PepperRotationService>();

var app = builder.Build();

if (ephemeralKey)
    app.Logger.LogWarning(
        "PepperMill is using an EPHEMERAL storage key (Development only) — stored peppers will not survive a restart. " +
        "Set PepperMill:StorageKeyBase64 (base64 of a 32-byte key) for persistence.");

app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("Liveness probe");

app.MapPepperEndpoints();

app.Run();

// Resolves the AES-256 master key: from config when present (required in Production), otherwise an
// ephemeral key in Development. Returns the key and whether it was ephemeral.
static (byte[] Key, bool Ephemeral) ResolveMasterKey(PepperMillOptions options, IWebHostEnvironment env)
{
    if (!string.IsNullOrWhiteSpace(options.StorageKeyBase64))
    {
        var key = Convert.FromBase64String(options.StorageKeyBase64);
        if (key.Length != 32)
            throw new InvalidOperationException("PepperMill:StorageKeyBase64 must decode to exactly 32 bytes.");
        return (key, false);
    }

    if (env.IsDevelopment())
        return (RandomNumberGenerator.GetBytes(32), true);

    throw new InvalidOperationException(
        "PepperMill:StorageKeyBase64 (base64 of a 32-byte key) is required outside Development.");
}

/// <summary>Exposed so integration tests can host the app via <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program { }
