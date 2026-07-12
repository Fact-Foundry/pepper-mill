using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Tests;

/// <summary>A settable clock for exercising epoch/rotation logic.</summary>
internal sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>An in-memory <see cref="IPepperStore"/> for fast service-level tests.</summary>
internal sealed class InMemoryPepperStore : IPepperStore
{
    private readonly Dictionary<(string TenantId, string SiteId), StoredPepper> _peppers = new();

    public Task<StoredPepper?> GetAsync(string tenantId, string siteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_peppers.GetValueOrDefault((tenantId, siteId)));

    public Task SaveAsync(StoredPepper pepper, CancellationToken cancellationToken = default)
    {
        _peppers[(pepper.TenantId, pepper.SiteId)] = pepper;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        _peppers.Remove((tenantId, siteId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredPepper>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredPepper>>(_peppers.Values.ToList());
}

/// <summary>An in-memory <see cref="ICredentialStore"/> for fast entitlement tests.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string TenantId, string SiteId), SiteCredential> _creds = new();

    public Task<SiteCredential?> GetAsync(string tenantId, string siteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_creds.GetValueOrDefault((tenantId, siteId)));

    public Task SaveAsync(SiteCredential credential, CancellationToken cancellationToken = default)
    {
        _creds[(credential.TenantId, credential.SiteId)] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        _creds.Remove((tenantId, siteId));
        return Task.CompletedTask;
    }
}
