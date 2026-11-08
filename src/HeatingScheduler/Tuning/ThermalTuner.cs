using System.Globalization;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Tuning;

/// <summary>
/// Reads the tracked rooms' current temperatures and measures their cooling and heating rates from the Sentio's
/// temperature history (see <see cref="ThermalEstimator"/>), and gives both to the <see cref="HeatingSchedule"/>. Run by
/// <see cref="PriceFetchService"/> whenever new prices arrive, so the rates are re-measured once a day.
/// When a rate can't be measured, the current one (initially from <see cref="ScheduleOptions"/>) stays in force.
/// </summary>
public sealed class ThermalTuner(
    IWavinClient wavin,
    HeatingSchedule schedule,
    IOptions<TuningOptions> tuningOptions,
    IOptions<ScheduleOptions> scheduleOptions,
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

    private const double SetpointTolerance = 0.05;

    public bool Enabled => tuningOptions.Value.Enabled;

    /// <summary>Reads the tracked rooms' current temperatures and gives them to the schedule. Returns the devices read.</summary>
    public async Task<IReadOnlyList<WavinDevice>> RefreshTemperaturesAsync(CancellationToken cancellationToken)
    {
        var devices = await wavin.GetDevicesAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var tracked = Tracked(devices);
        schedule.UpdateTemperatures(tracked
            .Where(x => x.Room.AirTemperature is not null)
            .Select(x => new RoomTemperature(x.Key, x.Room.DisplayName, x.Room.AirTemperature!.Value, now)));
        return devices;
    }

    /// <summary>Reads current temperatures, measures the rates from the room history and applies any change to the schedule.</summary>
    public async Task<TuningResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var devices = await RefreshTemperaturesAsync(cancellationToken);
        var end = timeProvider.GetUtcNow();
        var start = end - History - Context;

        var series = new List<RoomSeries>();
        foreach (var (key, room) in Tracked(devices))
        {
            var device = key[..key.LastIndexOf('/')];
            var rows = await wavin.GetRoomTimeSeriesAsync(device, room.IdText, start, end, BucketMinutes, cancellationToken);
            series.Add(new RoomSeries(key, room.DisplayName, ClassifyBySetpointChanges(rows, heatingOptions.Value)));
        }

        var result = ThermalEstimator.Estimate(series, new TuningSettings(CountFrom: end - History));
        var current = schedule.Rooms.ToDictionary(r => r.Key);
        var updates = new List<RoomRates>();
        var changes = new List<string>();
        foreach (var e in result.Rooms)
        {
            var model = current.GetValueOrDefault(e.Key);
            var cooling = e.CoolingPerHour;
            var heating = e.HeatingPerHour;
            if (cooling is not null || heating is not null)
            {
                updates.Add(new RoomRates(e.Key, e.Name, cooling, heating));
                changes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{e.Name}: cooling {model?.CoolingPerHour:0.000} → {cooling ?? model?.CoolingPerHour:0.000}°C/h, heating {model?.HeatingPerHour:0.000} → {heating ?? model?.HeatingPerHour:0.000}°C/h"));
            }
        }

        if (updates.Count == 0)
        {
            logger.LogInformation("No room rates could be measured; keeping the current ones. {Rooms}", Describe(result.Rooms));
            return result;
        }

        logger.LogInformation("Room rates from temperature history: {Changes}. Measured: {Rooms}",
            string.Join("; ", changes), Describe(result.Rooms));
        schedule.UpdateRates(updates);
        return result;
    }

    private List<(string Key, WavinRoom Room)> Tracked(IReadOnlyList<WavinDevice> devices)
    {
        var tracked = WavinSelection.TrackedRooms(devices, wavinOptions.Value.Rooms, scheduleOptions.Value.Rooms).ToList();
        if (tracked.Count == 0 && scheduleOptions.Value.Rooms.Count > 0)
        {
            logger.LogWarning(
                "None of the Schedule:Rooms ({Wanted}) are controlled rooms. Controlled rooms: {Rooms}",
                string.Join(", ", scheduleOptions.Value.Rooms),
                string.Join(", ", WavinSelection.TrackedRooms(devices, wavinOptions.Value.Rooms, []).Select(x => $"{x.Room.DisplayName} (id {x.Room.IdText})")));
        }

        return tracked;
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
                $"cooling {c:0.000}°C/h (median of {r.CoolingPeriods} fall{(r.CoolingPeriods == 1 ? "" : "s")})"));
        }

        if (r.HeatingPerHour is { } h)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"heating {h:0.000}°C/h (median of {r.HeatingPeriods} rise{(r.HeatingPeriods == 1 ? "" : "s")})"));
        }

        if (r.Note is not null)
        {
            parts.Add(r.Note);
        }

        return $"{r.Name}: {string.Join(", ", parts)}";
    }
}
