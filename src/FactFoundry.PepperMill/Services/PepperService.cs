namespace FactFoundry.PepperMill.Services;

/// <summary>The current pepper for a site, ready to hand back to an entitled server.</summary>
/// <param name="PepperBase64">The 256-bit pepper, base64-encoded.</param>
/// <param name="Epoch">The epoch it belongs to (<c>yyyy-MM</c>).</param>
/// <param name="RotatesAtUtc">When this pepper will rotate out.</param>
public sealed record PepperResult(string PepperBase64, string Epoch, DateTimeOffset RotatesAtUtc);

/// <summary>
/// Generates, serves, and rotates per-site peppers. Rotation is inherent: a fetch always returns
/// the <em>current</em> epoch's pepper, generating (and thereby replacing/destroying) a stale one.
/// </summary>
public sealed class PepperService
{
    private readonly IPepperStore _store;
    private readonly IClock _clock;

    /// <summary>Creates a new <see cref="PepperService"/>.</summary>
    public PepperService(IPepperStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>
    /// Returns the current pepper for a tenant's site, generating a fresh one (and destroying any
    /// prior epoch) when none exists or the stored one has aged out of the current epoch.
    /// </summary>
    public async Task<PepperResult> GetCurrentAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var epoch = Epoch.Current(_clock.UtcNow);
        var stored = await _store.GetAsync(tenantId, siteId, cancellationToken);

        if (stored is null || stored.Epoch != epoch.Id)
        {
            stored = new StoredPepper(tenantId, siteId, epoch.Id, Convert.ToBase64String(PepperGenerator.Generate()), _clock.UtcNow);
            await _store.SaveAsync(stored, cancellationToken);
        }

        return new PepperResult(stored.PepperBase64, stored.Epoch, epoch.RotatesAtUtc);
    }

    /// <summary>
    /// Forces a fresh pepper for a site immediately, destroying any current one (manual rotation).
    /// The new pepper belongs to the current epoch; a subsequent fetch returns it unchanged until the
    /// epoch rolls over. Creates the pepper if the site had none.
    /// </summary>
    public async Task<PepperResult> ForceRotateAsync(string tenantId, string siteId, CancellationToken cancellationToken = default)
    {
        var epoch = Epoch.Current(_clock.UtcNow);
        var fresh = new StoredPepper(tenantId, siteId, epoch.Id, Convert.ToBase64String(PepperGenerator.Generate()), _clock.UtcNow);
        await _store.SaveAsync(fresh, cancellationToken);
        return new PepperResult(fresh.PepperBase64, fresh.Epoch, epoch.RotatesAtUtc);
    }

    /// <summary>
    /// Rotates every stored pepper whose epoch is no longer current — generating a new one and
    /// destroying the old. Returns how many were rotated. Runs on a timer so destruction happens
    /// even for sites that aren't currently fetching.
    /// </summary>
    public async Task<int> RotateStaleAsync(CancellationToken cancellationToken = default)
    {
        var epoch = Epoch.Current(_clock.UtcNow);
        var rotated = 0;

        foreach (var stored in await _store.ListAsync(cancellationToken))
        {
            if (stored.Epoch == epoch.Id)
                continue;

            var fresh = new StoredPepper(stored.TenantId, stored.SiteId, epoch.Id, Convert.ToBase64String(PepperGenerator.Generate()), _clock.UtcNow);
            await _store.SaveAsync(fresh, cancellationToken);
            rotated++;
        }

        return rotated;
    }
}
