using HeatingScheduler.Notifications;
using HeatingScheduler.Octopus;
using HeatingScheduler.Tuning;

namespace HeatingScheduler.Scheduling;

/// <summary>
/// Service 1: every 30 minutes, fetches all published Agile prices and hands them to <see cref="HeatingSchedule"/>.
/// When new prices arrive (about once a day, and at startup) it re-tunes the rules with <see cref="ThermalTuner"/>.
/// After each check it emails the plan if it has changed (<see cref="PlanNotifier"/>).
/// </summary>
public sealed class PriceFetchService(
    IOctopusAgileClient octopus,
    HeatingSchedule schedule,
    ThermalTuner tuner,
    PlanNotifier notifier,
    TimeProvider timeProvider,
    ILogger<PriceFetchService> logger) : BackgroundService
{
    /// <summary>How often to check for new prices.</summary>
    public static readonly TimeSpan FetchInterval = TimeSpan.FromMinutes(30);

    private bool _tuningDue = true;
    private TuningResult? _lastTuning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FetchInterval, timeProvider);
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
        var reasons = new List<string>();
        var now = timeProvider.GetUtcNow();
        var current = Slots.StartFor(now);

        var knownUntil = schedule.PricesKnownUntil(current);
        var rates = await octopus.GetRatesAsync(current, current + HeatingSchedule.MaxHorizon, cancellationToken);
        schedule.UpdatePrices(rates, now);

        var newKnownUntil = schedule.PricesKnownUntil(current);
        if (newKnownUntil > knownUntil)
        {
            logger.LogInformation("Agile prices now known until {Until} (UK)", Slots.Uk(newKnownUntil));
            reasons.Add($"new Agile prices until {Slots.Uk(newKnownUntil)}");
            _tuningDue = true;
        }
        else
        {
            logger.LogDebug("No new Agile prices yet; known until {Until} (UK)", Slots.Uk(newKnownUntil));
        }

        if (_tuningDue && tuner.Enabled)
        {
            var rulesBefore = schedule.Rules;
            try
            {
                _lastTuning = await tuner.RunOnceAsync(cancellationToken);
                _tuningDue = false;
                if (schedule.Rules != rulesBefore)
                {
                    reasons.Add("tuning updated the rules");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to work out heating rules from room temperatures; will retry at the next price check");
            }
        }

        await notifier.NotifyIfChangedAsync(schedule.GetSnapshot(), reasons, tuner.Enabled ? _lastTuning : null, cancellationToken);
    }
}
