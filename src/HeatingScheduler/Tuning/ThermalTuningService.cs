using System.Globalization;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Tuning;

/// <summary>
/// Periodically works out the make-up ratio and maximum off period from the Sentio's room temperature history
/// (see <see cref="ThermalEstimator"/>) and applies them to the <see cref="HeatingSchedule"/>.
/// When a value can't be measured, the current one (initially from <see cref="ScheduleOptions"/>) stays in force.
/// </summary>
public sealed class ThermalTuningService(
    IWavinClient wavin,
    HeatingSchedule schedule,
    IOptions<TuningOptions> tuningOptions,
    IOptions<WavinOptions> wavinOptions,
    IOptions<HeatingOptions> heatingOptions,
    TimeProvider timeProvider,
    ILogger<ThermalTuningService> logger) : BackgroundService
{
    /// <summary>Bucket size requested from the Sentio history.</summary>
    public const int BucketMinutes = 5;

    /// <summary>How much room history is analysed.</summary>
    public static readonly TimeSpan History = TimeSpan.FromHours(24);

    /// <summary>How often the rules are recalculated.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>A newly measured ratio is only applied when it differs from the current one by more than this fraction.</summary>
    public const double RatioChangeThreshold = 0.1;

    private const double SetpointTolerance = 0.05;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
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
                logger.LogError(ex, "Failed to work out heating rules from room temperatures; will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<TuningResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var history = await FetchHistoryAsync(cancellationToken);

        var settings = new TuningSettings(tuningOptions.Value.MinTemperature, heatingOptions.Value.OnTemperature);

        var result = ThermalEstimator.Estimate(history, settings);
        var current = schedule.Rules;
        var ratio = result.OnToOffRatio is double measured
            && Math.Abs(measured - current.OnToOffRatio) > RatioChangeThreshold * current.OnToOffRatio
                ? measured
                : current.OnToOffRatio;
        var maxOffSlots = result.MaxOffMinutes is int m ? Slots.FromMinutes(m) : current.MaxOffSlots;

        if (ratio.Equals(current.OnToOffRatio) && maxOffSlots == current.MaxOffSlots)
        {
            logger.LogDebug("Heating rules unchanged: ratio {Ratio}, max off {MaxOff} min. Rooms: {Rooms}",
                ratio, maxOffSlots * 30, Describe(result.Rooms));
            return result;
        }

        logger.LogInformation(
            "Heating rules from room temperatures: on-to-off ratio {OldRatio} → {Ratio}, max off {OldMaxOff} → {MaxOff} min. Rooms: {Rooms}",
            current.OnToOffRatio, ratio, current.MaxOffSlots * 30, maxOffSlots * 30, Describe(result.Rooms));
        schedule.UpdateRules(ratio, maxOffSlots);
        return result;
    }

    /// <summary>
    /// Fetches the last <see cref="History"/> of each room to tune against
    /// (<see cref="TuningOptions.Rooms"/>, or every controlled room). A reading counts as on or off
    /// when its setpoint is the on or off temperature; other setpoints (manual changes, or a bucket spanning a switch)
    /// are left out.
    /// </summary>
    private async Task<IReadOnlyList<RoomSeries>> FetchHistoryAsync(CancellationToken cancellationToken)
    {
        var end = timeProvider.GetUtcNow();
        var start = end - History;
        var heating = heatingOptions.Value;

        var series = new List<RoomSeries>();
        foreach (var device in await wavin.GetDevicesAsync(cancellationToken))
        {
            var controlled = WavinSelection.SelectRooms(device.Rooms, wavinOptions.Value.Rooms).Where(r => !r.IsInVacationMode).ToList();
            var rooms = WavinSelection.SelectRooms(controlled, tuningOptions.Value.Rooms).ToList();
            if (rooms.Count == 0 && tuningOptions.Value.Rooms.Count > 0)
            {
                logger.LogWarning(
                    "None of the Tuning:Rooms ({Wanted}) are controlled rooms on {Device}. Controlled rooms: {Rooms}",
                    string.Join(", ", tuningOptions.Value.Rooms), device.Name,
                    string.Join(", ", controlled.Select(r => $"{r.DisplayName} (id {r.IdText})")));
            }

            foreach (var room in rooms)
            {
                var rows = await wavin.GetRoomTimeSeriesAsync(device.Name, room.IdText, start, end, BucketMinutes, cancellationToken);
                var readings = rows
                    .Where(r => r.AirTemperature is not null && r.SetpointTemperature is not null)
                    .Select(r => (Row: r, State: Classify(r.SetpointTemperature!.Value, heating)))
                    .Where(x => x.State is not null)
                    .Select(x => new RoomReading(x.Row.Timestamp, x.Row.AirTemperature!.Value, x.State!.Value))
                    .OrderBy(r => r.At)
                    .ToList();
                series.Add(new RoomSeries($"{device.Name}/{room.IdText}", room.DisplayName, readings));
            }
        }

        return series;
    }

    private static HeatingState? Classify(double setpoint, HeatingOptions heating) =>
        Math.Abs(setpoint - heating.OnTemperature) <= SetpointTolerance ? HeatingState.On
        : Math.Abs(setpoint - heating.OffTemperature) <= SetpointTolerance ? HeatingState.Off
        : null;

    private static string Describe(IReadOnlyList<RoomEstimate> rooms) => string.Join("; ", rooms.Select(r =>
    {
        var parts = new List<string>();
        if (r.CoolingPerHour is { } c)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"cooling {c:0.00}°C/h over {r.OffDuration!.Value.TotalHours:0.#}h off from {r.OffStartTemperature:0.0}°C"));
            parts.Add(r.MaxOffMinutes is { } mo && double.IsFinite(mo)
                ? string.Create(CultureInfo.InvariantCulture, $"max off {mo:0} min")
                : "max off unlimited");
        }

        if (r.HeatingPerHour is { } h)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"heating {h:0.00}°C/h (median of {r.HeatingPeriods} on period{(r.HeatingPeriods == 1 ? "" : "s")})"));
        }

        if (r.OnToOffRatio is { } ratio)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"ratio {ratio:0.00}"));
        }

        if (r.Note is not null)
        {
            parts.Add(r.Note);
        }

        return $"{r.Name}: {string.Join(", ", parts)}";
    }));
}
