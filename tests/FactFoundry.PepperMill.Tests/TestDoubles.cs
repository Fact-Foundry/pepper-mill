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
