using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Scheduling;

/// <summary>
/// Service 1: periodically fetches Agile prices for the rolling planning horizon and hands them to
/// <see cref="HeatingSchedule"/>, which re-plans the on/off schedule.
/// </summary>
public sealed class PriceFetchService(
    IOctopusAgileClient octopus,
    HeatingSchedule schedule,
    IOptions<OctopusOptions> octopusOptions,
    IOptions<ScheduleOptions> scheduleOptions,
    TimeProvider timeProvider,
    ILogger<PriceFetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(octopusOptions.Value.FetchIntervalMinutes), timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch Agile prices; will retry on next tick");
            }

            schedule.MarkFirstAttemptComplete();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var current = Slots.StartFor(now);
        var horizonEnd = current + TimeSpan.FromHours(scheduleOptions.Value.HorizonHours);

        var knownUntil = schedule.PricesKnownUntil(current);
        if (knownUntil >= horizonEnd)
        {
            logger.LogDebug("Prices already known for the whole horizon (to {End})", Slots.Uk(horizonEnd));
            return;
        }

        var rates = await octopus.GetRatesAsync(current, horizonEnd, cancellationToken);
        schedule.UpdatePrices(rates, now);

        var newKnownUntil = schedule.PricesKnownUntil(current);
        if (newKnownUntil > knownUntil)
        {
            logger.LogInformation("Agile prices now known until {Until} (UK)", Slots.Uk(newKnownUntil));
        }
        else
        {
            logger.LogDebug("No new Agile prices yet; known until {Until} (UK)", Slots.Uk(newKnownUntil));
        }
    }
}
