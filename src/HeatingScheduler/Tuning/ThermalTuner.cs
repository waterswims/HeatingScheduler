using System.Globalization;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Tuning;

/// <summary>
/// Works out the make-up ratio and maximum off period from the Sentio's room temperature history
/// (see <see cref="ThermalEstimator"/>) and applies them to the <see cref="HeatingSchedule"/>. Run by
/// <see cref="PriceFetchService"/> whenever new prices arrive, so the plan is re-tuned once a day.
/// When a value can't be measured, the current one (initially from <see cref="ScheduleOptions"/>) stays in force.
/// </summary>
public sealed class ThermalTuner(
    IWavinClient wavin,
    HeatingSchedule schedule,
    IOptions<TuningOptions> tuningOptions,
    IOptions<WavinOptions> wavinOptions,
    IOptions<HeatingOptions> heatingOptions,
    TimeProvider timeProvider,
    ILogger<ThermalTuner> logger)
{
    /// <summary>Bucket size requested from the Sentio history.</summary>
    public const int BucketMinutes = 5;

    /// <summary>How much room history is analysed: enough for several heating cycles.</summary>
    public static readonly TimeSpan History = TimeSpan.FromHours(72);

    /// <summary>
    /// Extra history fetched before <see cref="History"/>, only to confirm the turning points near the start of the
    /// window; movements starting in it are not counted.
    /// </summary>
    public static readonly TimeSpan Context = TimeSpan.FromHours(12);

    /// <summary>A newly measured ratio is only applied when it differs from the current one by more than this fraction.</summary>
    public const double RatioChangeThreshold = 0.1;

    private const double SetpointTolerance = 0.05;

    public bool Enabled => tuningOptions.Value.Enabled;

    /// <summary>Fetches the room history, works out the rules and applies any change to the schedule.</summary>
    public async Task<TuningResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var history = await FetchHistoryAsync(cancellationToken);

        var settings = new TuningSettings(tuningOptions.Value.MinTemperature, CountFrom: timeProvider.GetUtcNow() - History);

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
    /// Fetches the last <see cref="History"/> (plus <see cref="Context"/>) of each room to tune against
    /// (<see cref="TuningOptions.Rooms"/>, or every controlled room), and labels each reading on or off with
    /// <see cref="ClassifyBySetpointChanges"/>.
    /// </summary>
    private async Task<IReadOnlyList<RoomSeries>> FetchHistoryAsync(CancellationToken cancellationToken)
    {
        var end = timeProvider.GetUtcNow();
        var start = end - History - Context;
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
                var readings = ClassifyBySetpointChanges(rows, heating);
                series.Add(new RoomSeries($"{device.Name}/{room.IdText}", room.DisplayName, readings));
            }
        }

        return series;
    }

    /// <summary>
    /// Labels each reading on or off from changes in the room's setpoint: a rise turns the heating on and a fall turns
    /// it off, whatever the temperatures. Changing the on or off temperature therefore doesn't invalidate the history.
    /// Readings before the first change take the opposite state to that change (a first rise means it was off before).
    /// If the setpoint never changes, the reading is matched against the current on and off temperatures, or left out
    /// if it matches neither. Readings without an air temperature are left out but still followed for changes.
    /// </summary>
    public static List<RoomReading> ClassifyBySetpointChanges(IReadOnlyList<RoomTimeSeriesRow> rows, HeatingOptions heating)
    {
        var ordered = rows.Where(r => r.SetpointTemperature is not null).OrderBy(r => r.Timestamp).ToList();
        var states = new HeatingState?[ordered.Count];
        HeatingState? state = null;
        var firstChange = -1;
        for (var i = 1; i < ordered.Count; i++)
        {
            var change = ordered[i].SetpointTemperature!.Value - ordered[i - 1].SetpointTemperature!.Value;
            if (Math.Abs(change) > SetpointTolerance)
            {
                state = change > 0 ? HeatingState.On : HeatingState.Off;
                if (firstChange < 0)
                {
                    firstChange = i;
                }
            }

            states[i] = state;
        }

        if (firstChange < 0)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                states[i] = MatchConfigured(ordered[i].SetpointTemperature!.Value, heating);
            }
        }
        else
        {
            var before = states[firstChange] == HeatingState.On ? HeatingState.Off : HeatingState.On;
            for (var i = 0; i < firstChange; i++)
            {
                states[i] = before;
            }
        }

        var readings = new List<RoomReading>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].AirTemperature is { } air && states[i] is { } s)
            {
                readings.Add(new RoomReading(ordered[i].Timestamp, air, s));
            }
        }

        return readings;
    }

    private static HeatingState? MatchConfigured(double setpoint, HeatingOptions heating) =>
        Math.Abs(setpoint - heating.OnTemperature) <= SetpointTolerance ? HeatingState.On
        : Math.Abs(setpoint - heating.OffTemperature) <= SetpointTolerance ? HeatingState.Off
        : null;

    private static string Describe(IReadOnlyList<RoomEstimate> rooms) => string.Join("; ", rooms.Select(DescribeRoom));

    /// <summary>One line describing what was measured for a room.</summary>
    public static string DescribeRoom(RoomEstimate r)
    {
        var parts = new List<string>();
        if (r.CoolingPerHour is { } c)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"cooling {c:0.00}°C/h (median of {r.CoolingPeriods} fall{(r.CoolingPeriods == 1 ? "" : "s")}, from {r.FallStartTemperature:0.0}°C)"));
            parts.Add(r.MaxOffMinutes is { } mo && double.IsFinite(mo)
                ? string.Create(CultureInfo.InvariantCulture, $"max off {mo:0} min")
                : "max off unlimited");
        }

        if (r.HeatingPerHour is { } h)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"heating {h:0.00}°C/h (median of {r.HeatingPeriods} rise{(r.HeatingPeriods == 1 ? "" : "s")})"));
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
    }
}
