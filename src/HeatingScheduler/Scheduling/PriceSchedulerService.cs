using System.Globalization;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Scheduling;

/// <summary>
/// Service 1: periodically fetches Agile prices and, once all slots for an Agile day are known,
/// selects the cheapest slots for that day and stores them in <see cref="HeatingSchedule"/>.
/// </summary>
public sealed class PriceSchedulerService(
    IOctopusAgileClient octopus,
    HeatingSchedule schedule,
    IOptions<OctopusOptions> octopusOptions,
    IOptions<ScheduleOptions> scheduleOptions,
    TimeProvider timeProvider,
    ILogger<PriceSchedulerService> logger) : BackgroundService
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
        schedule.RemoveDaysBefore(now);

        var today = AgileDay.Containing(now);
        var tomorrow = today.Next;
        var pending = new[] { today, tomorrow }.Where(d => !schedule.HasDay(d)).ToList();
        if (pending.Count == 0)
        {
            logger.LogDebug("Schedules for {Today} and {Tomorrow} already computed", today, tomorrow);
            return;
        }

        var rates = await octopus.GetRatesAsync(pending[0].StartUtc, pending[^1].EndUtc, cancellationToken);
        var byStart = rates.ToDictionary(r => r.ValidFrom);
        logger.LogInformation("Fetched {Count} Agile rates from {From:u} to {To:u}", rates.Count, pending[0].StartUtc, pending[^1].EndUtc);

        foreach (var day in pending)
        {
            var daySlots = day.SlotStarts().Select(s => byStart.GetValueOrDefault(s)).ToList();
            if (daySlots.Any(s => s is null))
            {
                logger.LogInformation(
                    "{Day}: {Have}/{Need} prices available, not scheduling yet",
                    day, daySlots.Count(s => s is not null), day.SlotCount);
                continue;
            }

            var daySchedule = CheapestSlotSelector.Select(day, daySlots!, scheduleOptions.Value.OnPercentage);
            schedule.SetDay(daySchedule);
            LogSchedule(daySchedule);
        }
    }

    private void LogSchedule(DaySchedule s)
    {
        var onTimes = s.OnSlotStarts
            .Order()
            .Select(t => TimeZoneInfo.ConvertTime(t, AgileDay.UkTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture));
        logger.LogInformation(
            "{Day}: heating ON for {On}/{Total} slots (avg {OnPrice:F2}p vs day avg {DayPrice:F2}p/kWh). ON slots (UK time): {Slots}",
            s.Day, s.OnSlotStarts.Count, s.SlotCount, s.AverageOnPrice, s.AverageDayPrice, string.Join(", ", onTimes));
    }
}
