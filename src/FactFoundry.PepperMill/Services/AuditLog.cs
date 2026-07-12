using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FactFoundry.PepperMill.Services;

/// <summary>An audit record. Never contains pepper material — only metadata about an event.</summary>
/// <param name="Timestamp">When it happened (UTC).</param>
/// <param name="Event">Event kind, e.g. "pepper.fetch", "pepper.revoke".</param>
/// <param name="TenantId">The tenant involved.</param>
/// <param name="SiteId">The site involved (unique within the tenant); every event is site-scoped.</param>
/// <param name="Detail">Optional free-text context (never a secret).</param>
public sealed record AuditEntry(DateTimeOffset Timestamp, string Event, string TenantId, string SiteId, string? Detail = null);

/// <summary>Appends audit records. An external provider could delegate this elsewhere.</summary>
public interface IAuditLog
{
    /// <summary>Records an audit entry.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Appends audit records as JSON lines to a file alongside the pepper store.</summary>
public sealed class FileAuditLog : IAuditLog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the audit log, ensuring its directory exists.</summary>
    public FileAuditLog(IOptions<PepperMillOptions> options)
    {
        Directory.CreateDirectory(options.Value.StorePath);
        _path = Path.Combine(options.Value.StorePath, "audit.log");
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_path, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
