using HeatingScheduler.Notifications;
using HeatingScheduler.Octopus;
using HeatingScheduler.Tuning;

namespace HeatingScheduler.Scheduling;

/// <summary>
/// Service 1: every 30 minutes, fetches all published Agile prices and hands them to <see cref="HeatingSchedule"/>.
/// Only when new prices arrive (about once a day, and at startup) does it re-measure the room rates with
/// <see cref="ThermalTuner"/> (which also reads the current room temperatures) and email the plan
/// (<see cref="PlanNotifier"/>). If either fails, it is retried at the next check. The plan itself is re-made hourly by
/// <see cref="HeatingSchedule"/>, independently of this.
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
    private readonly List<string> _emailReasons = [];
    private bool _emailDue;

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
        var fetchedUntil = rates.Count > 0 ? rates.Max(r => r.ValidTo) : knownUntil;
        var newPrices = fetchedUntil > knownUntil;
        if (newPrices)
        {
            _tuningDue = true;
            _emailDue = true;
        }

        // Rates and current temperatures first, so the plan made with the new prices starts from the real rooms.
        if (_tuningDue)
        {
            var roomsBefore = schedule.Rooms;
            try
            {
                if (tuner.Enabled)
                {
                    _lastTuning = await tuner.RunOnceAsync(cancellationToken);
                }
                else
                {
                    await tuner.RefreshTemperaturesAsync(cancellationToken);
                }

                _tuningDue = false;
                if (!roomsBefore.Select(r => (r.Key, r.CoolingPerHour, r.HeatingPerHour))
                        .SequenceEqual(schedule.Rooms.Select(r => (r.Key, r.CoolingPerHour, r.HeatingPerHour))))
                {
                    reasons.Add("room rates re-measured");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to read room temperatures or measure room rates; will retry at the next price check");
            }
        }

        schedule.UpdatePrices(rates, now);

        var newKnownUntil = schedule.PricesKnownUntil(current);
        if (newKnownUntil > knownUntil)
        {
            logger.LogInformation("Agile prices now known until {Until} (UK)", Slots.Uk(newKnownUntil));
            reasons.Insert(0, $"new Agile prices until {Slots.Uk(newKnownUntil)}");
        }
        else
        {
            logger.LogDebug("No new Agile prices yet; known until {Until} (UK)", Slots.Uk(newKnownUntil));
        }

        _emailReasons.AddRange(reasons.Where(r => !_emailReasons.Contains(r)));
        if (_emailDue && await notifier.NotifyAsync(_emailReasons, tuner.Enabled ? _lastTuning : null, cancellationToken))
        {
            _emailDue = false;
            _emailReasons.Clear();
        }
        else if (!_emailDue)
        {
            _emailReasons.Clear();
        }
    }
}
