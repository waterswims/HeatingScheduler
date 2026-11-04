using HeatingScheduler.Options;

namespace HeatingScheduler.Tuning;

/// <summary>One air-temperature reading, with whether the heating was on or off at the time.</summary>
public sealed record RoomReading(DateTimeOffset At, double AirTemperature, HeatingState State);

public sealed record RoomSeries(string Key, string Name, IReadOnlyList<RoomReading> Readings);

public sealed record TuningSettings(double MinTemperature, double OnTemperature);

/// <summary>What was measured for one room. Rates are in °C per hour; null means it could not be measured.</summary>
public sealed record RoomEstimate(
    string Name,
    TimeSpan? OffDuration,
    double? OffStartTemperature,
    double? CoolingPerHour,
    double? MaxOffMinutes,
    TimeSpan? HeatingDuration,
    double? HeatingPerHour,
    double? OnToOffRatio,
    string? Note);

/// <summary>
/// The combined result: the most restrictive room decides. Null values mean "not enough data; keep the current value".
/// </summary>
public sealed record TuningResult(double? OnToOffRatio, int? MaxOffMinutes, IReadOnlyList<RoomEstimate> Rooms);

/// <summary>
/// Works out the maximum off period and the make-up ratio from recorded room temperatures.
/// <list type="number">
/// <item><b>Cooling:</b> the longest off period in the history; the cooling rate is the fitted (least-squares)
/// temperature drop per minute over that period.</item>
/// <item><b>Max off:</b> (temperature when that off period started − minimum temperature) ÷ cooling rate.</item>
/// <item><b>Heating:</b> the on period after the most recent off period, from switch-on until the room reached
/// the on temperature (or the on period ended, if it never did); the heating rate is the fitted rise per minute.</item>
/// <item><b>Ratio:</b> cooling rate ÷ heating rate: the on time needed to put back the heat lost per minute off.</item>
/// </list>
/// Across rooms the shortest max off and the highest ratio are used, then kept within safety bounds.
/// </summary>
public static class ThermalEstimator
{
    /// <summary>Off periods shorter than this are not used to measure cooling.</summary>
    public static readonly TimeSpan MinOffPeriod = TimeSpan.FromMinutes(60);

    /// <summary>Range the maximum off period is kept within (minutes).</summary>
    public const int MaxOffLowerBoundMinutes = 60, MaxOffUpperBoundMinutes = 720;

    /// <summary>Range the make-up ratio is kept within.</summary>
    public const double RatioLowerBound = 0.05, RatioUpperBound = 3.0;

    /// <summary>Readings further apart than this are treated as a gap (e.g. the Wavin API was unreachable).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(20);

    /// <summary>Rates below this (°C per minute, i.e. 0.03 °C/h) are treated as no measurable change.</summary>
    public const double MinRatePerMinute = 0.0005;

    private const double ReachedTolerance = 0.1;
    private static readonly TimeSpan MinHeatingWindow = TimeSpan.FromMinutes(15);

    private sealed record Segment(HeatingState State, List<RoomReading> Readings, DateTimeOffset End, bool FollowsPrevious)
    {
        public DateTimeOffset Start => Readings[0].At;
        public TimeSpan Duration => End - Start;
    }

    public static TuningResult Estimate(IReadOnlyList<RoomSeries> rooms, TuningSettings settings)
    {
        var estimates = rooms.Select(r => EstimateRoom(r, settings)).ToList();

        var maxOffs = estimates.Where(e => e.MaxOffMinutes is not null).Select(e => e.MaxOffMinutes!.Value).ToList();
        int? maxOff = maxOffs.Count == 0
            ? null
            : Math.Clamp(
                // Round down to whole slots, allowing a minute of measurement noise (e.g. 239.9 min → 240).
                (int)Math.Floor((Math.Min(maxOffs.Min(), MaxOffUpperBoundMinutes) + 1) / 30) * 30,
                MaxOffLowerBoundMinutes,
                MaxOffUpperBoundMinutes);

        var ratios = estimates.Where(e => e.OnToOffRatio is not null).Select(e => e.OnToOffRatio!.Value).ToList();
        double? ratio = ratios.Count == 0
            ? null
            : Math.Round(Math.Clamp(ratios.Max(), RatioLowerBound, RatioUpperBound), 3);

        return new TuningResult(ratio, maxOff, estimates);
    }

    private static RoomEstimate EstimateRoom(RoomSeries room, TuningSettings settings)
    {
        var segments = Split(room.Readings);

        var longestOff = segments
            .Where(s => s.State == HeatingState.Off && s.Duration >= MinOffPeriod && s.Readings.Count >= 2)
            .MaxBy(s => s.Duration);

        if (longestOff is null)
        {
            return new RoomEstimate(room.Name, null, null, null, null, null, null, null,
                $"no off period of {MinOffPeriod.TotalMinutes:0} min or more yet");
        }

        var coolingPerMinute = -SlopePerMinute(longestOff.Readings);
        var startTemperature = longestOff.Readings[0].AirTemperature;
        double maxOffMinutes;
        string? note = null;
        if (coolingPerMinute < MinRatePerMinute)
        {
            maxOffMinutes = double.PositiveInfinity;
            note = "no measurable cooling";
        }
        else
        {
            maxOffMinutes = Math.Max(0, (startTemperature - settings.MinTemperature) / coolingPerMinute);
        }

        // Heating: the on period after the most recent off period.
        TimeSpan? heatingDuration = null;
        double? heatingPerHour = null;
        double? ratio = null;
        var lastOn = Enumerable.Range(1, Math.Max(0, segments.Count - 1))
            .Reverse()
            .Select(i => (Off: segments[i - 1], On: segments[i]))
            .FirstOrDefault(p => p.On.State == HeatingState.On && p.Off.State == HeatingState.Off && p.On.FollowsPrevious)
            .On;

        if (lastOn is null)
        {
            note ??= "no on period after an off period yet";
        }
        else
        {
            var reached = lastOn.Readings.FindIndex(r => r.AirTemperature >= settings.OnTemperature - ReachedTolerance);
            var window = reached >= 0 ? lastOn.Readings.Take(reached + 1).ToList() : lastOn.Readings;
            var windowDuration = window[^1].At - window[0].At;
            heatingDuration = windowDuration;

            if (window.Count < 2 || windowDuration < MinHeatingWindow)
            {
                note ??= reached == 0 ? "room was already at the on temperature" : "on period too short to measure heating";
            }
            else
            {
                var heatingPerMinute = SlopePerMinute(window);
                heatingPerHour = heatingPerMinute * 60;
                if (heatingPerMinute < MinRatePerMinute)
                {
                    note ??= "no measurable warming during the last on period";
                }
                else if (coolingPerMinute >= MinRatePerMinute)
                {
                    ratio = coolingPerMinute / heatingPerMinute;
                }
            }
        }

        return new RoomEstimate(
            room.Name,
            longestOff.Duration,
            startTemperature,
            coolingPerMinute * 60,
            maxOffMinutes,
            heatingDuration,
            heatingPerHour,
            ratio,
            note);
    }

    /// <summary>Splits readings into runs of the same heating state, also breaking at gaps longer than <see cref="MaxGap"/>.</summary>
    private static List<Segment> Split(IReadOnlyList<RoomReading> readings)
    {
        var segments = new List<Segment>();
        List<RoomReading>? current = null;
        var follows = false;
        for (var i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            var gap = i > 0 && r.At - readings[i - 1].At > MaxGap;
            if (current is null || r.State != current[0].State || gap)
            {
                if (current is not null)
                {
                    // A segment ends when the next one starts, unless there was a gap.
                    segments.Add(new Segment(current[0].State, current, gap ? current[^1].At : r.At, follows));
                }

                follows = current is not null && !gap;
                current = [];
            }

            current.Add(r);
        }

        if (current is not null)
        {
            segments.Add(new Segment(current[0].State, current, current[^1].At, follows));
        }

        return segments;
    }

    /// <summary>Least-squares slope of air temperature against time, in °C per minute.</summary>
    private static double SlopePerMinute(IReadOnlyList<RoomReading> readings)
    {
        var t0 = readings[0].At;
        var xs = readings.Select(r => (r.At - t0).TotalMinutes).ToList();
        var ys = readings.Select(r => r.AirTemperature).ToList();
        var xMean = xs.Average();
        var yMean = ys.Average();
        var sxx = xs.Sum(x => (x - xMean) * (x - xMean));
        if (sxx == 0)
        {
            return 0;
        }

        var sxy = xs.Zip(ys).Sum(p => (p.First - xMean) * (p.Second - yMean));
        return sxy / sxx;
    }
}
