namespace FactFoundry.PepperMill.Services;

/// <summary>
/// A monthly pepper epoch. "Returning visitor" on the TelemetryForge side means "returning within
/// the current epoch"; at each boundary the pepper rotates and the prior one is destroyed.
/// </summary>
/// <param name="Id">The epoch identifier, <c>yyyy-MM</c> (UTC).</param>
/// <param name="RotatesAtUtc">When this epoch ends and the next pepper takes over (1st of next month, 00:00 UTC).</param>
public readonly record struct Epoch(string Id, DateTimeOffset RotatesAtUtc)
{
    /// <summary>The epoch containing the given instant.</summary>
    public static Epoch Current(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new Epoch($"{now.Year:D4}-{now.Month:D2}", start.AddMonths(1));
    }
}
