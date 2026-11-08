using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>How often the sweeper runs, from <c>EXPIRY_SWEEP_INTERVAL_SECONDS</c> (default 2).</summary>
public sealed record SweeperSettings(TimeSpan Interval)
{
    public const string ConfigKey = "EXPIRY_SWEEP_INTERVAL_SECONDS";
    public const int DefaultSeconds = 2;

    public static SweeperSettings From(IConfiguration configuration) =>
        new(SecondsSetting.Read(configuration, ConfigKey, DefaultSeconds));
}

/// <summary>
/// Writes EXPIRED to holds that are still stored as HELD after their expiry, so the stored state catches up without any
/// API call. Reads already report such holds as EXPIRED and never count them as held, so this is about the database
/// being truthful, not about seat counts. Each run is one conditional UPDATE: idempotent, and safe with any number of
/// backend instances sweeping at once (a row can only move HELD -> EXPIRED once, and confirm/cancel use the same guard).
/// </summary>
public sealed class ExpirySweeper(
    IServiceScopeFactory scopes, TimeProvider clock, SweeperSettings settings, ILogger<ExpirySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval, clock);
        try
        {
            do
            {
                await SweepSafelyAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>Expires every lapsed HELD hold and returns how many rows changed. One run of the loop; also callable directly.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        return await HoldExpiry.ExpireAllLapsedAsync(db, clock.GetUtcNow().UtcDateTime, ct);
    }

    // A database that is briefly unreachable must not take the whole API down (an exception escaping a
    // BackgroundService stops the host), so log and try again on the next tick.
    private async Task SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            var expired = await SweepOnceAsync(ct);
            if (expired > 0)
            {
                logger.LogInformation("Expired {Count} lapsed hold(s)", expired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Expiry sweep failed; will retry in {Interval}", settings.Interval);
        }
    }
}
