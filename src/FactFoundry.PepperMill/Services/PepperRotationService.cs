namespace FactFoundry.PepperMill.Services;

/// <summary>
/// Background worker that rotates peppers out of stale epochs on a timer, so the destruction
/// ceremony happens on schedule even for sites that aren't actively fetching. Fetches already
/// rotate lazily; this guarantees the prior epoch is destroyed promptly after each month boundary.
/// </summary>
public sealed class PepperRotationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly PepperService _peppers;
    private readonly ILogger<PepperRotationService> _logger;

    /// <summary>Creates a new <see cref="PepperRotationService"/>.</summary>
    public PepperRotationService(PepperService peppers, ILogger<PepperRotationService> logger)
    {
        _peppers = peppers;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rotated = await _peppers.RotateStaleAsync(stoppingToken);
                if (rotated > 0)
                    _logger.LogInformation("Rotated {Count} peppers into the current epoch (prior peppers destroyed).", rotated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pepper rotation pass failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
