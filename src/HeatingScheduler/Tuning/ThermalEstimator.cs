using HeatingScheduler.Options;

namespace HeatingScheduler.Tuning;

/// <summary>One air-temperature reading, with whether the heating was on or off at the time.</summary>
public sealed record RoomReading(DateTimeOffset At, double AirTemperature, HeatingState State);

public sealed record RoomSeries(string Key, string Name, IReadOnlyList<RoomReading> Readings);

/// <param name="CountFrom">
/// Only rises and falls starting at or after this time are counted. Readings before it are context, used to confirm the
/// turning points near the start of the window.
/// </param>
public sealed record TuningSettings(DateTimeOffset? CountFrom = null);

/// <summary>What was measured for one room. Rates are in °C per hour; null means it could not be measured.</summary>
public sealed record RoomEstimate(
    string Key,
    string Name,
    int CoolingPeriods,
    double? CoolingPerHour,
    int HeatingPeriods,
    double? HeatingPerHour,
    string? Note);

public sealed record TuningResult(IReadOnlyList<RoomEstimate> Rooms);

/// <summary>
/// Works out each room's cooling rate (heating off) and heating rate (heating on) from recorded temperatures, measuring
/// between the temperature's turning points rather than between switching times. With underfloor heating the air
/// temperature keeps falling for a while after the heating comes on (and rising after it goes off); measuring from the
/// turning points leaves that lag out.
/// <list type="number">
/// <item><b>Turning points:</b> the readings are smoothed (median over <see cref="SmoothingWindow"/>), then a peak or
/// trough is confirmed once the temperature has moved at least <see cref="MinSwing"/> back from it. Rises or falls
/// shorter than <see cref="MinLeg"/> (an oven, a shower, the sun through a window) are removed, merging the movement
/// either side of them.</item>
/// <item><b>Which movements count:</b> a rise counts as heating when the heating was on for at least
/// <see cref="HeatingOnFraction"/> of it; a fall counts as cooling when the heating was on for at most
/// <see cref="CoolingOnFraction"/> of it. Movements with the heating mixed, or with a gap in the readings, are left out.
/// Only movements between two confirmed turning points are used, so neither the one in progress nor one cut off by the
/// start of the history window is measured.</item>
/// <item><b>Rates:</b> each rise or fall is measured by a straight-line (least-squares) fit through its raw readings;
/// the heating and cooling rates are the medians over the counted rises and falls. A rate below
/// <see cref="MinRatePerMinute"/> isn't measurable and is reported as null.</item>
/// </list>
/// </summary>
public static class ThermalEstimator
{
    /// <summary>Smallest movement between a peak and a trough that counts as a turning point (°C).</summary>
    public const double MinSwing = 0.2;

    /// <summary>Rises and falls shorter than this are treated as short disturbances and removed.</summary>
    public static readonly TimeSpan MinLeg = TimeSpan.FromMinutes(60);

    /// <summary>Width of the centred median used to smooth the readings.</summary>
    public static readonly TimeSpan SmoothingWindow = TimeSpan.FromMinutes(30);

    /// <summary>A rise counts as heating when the heating was on for at least this fraction of it.</summary>
    public const double HeatingOnFraction = 0.7;

    /// <summary>A fall counts as cooling when the heating was on for at most this fraction of it.</summary>
    public const double CoolingOnFraction = 0.3;

    /// <summary>Readings further apart than this are treated as a gap (e.g. the Wavin API was unreachable).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(20);

    /// <summary>Rates below this (°C per minute, i.e. 0.03 °C/h) are treated as no measurable change.</summary>
    public const double MinRatePerMinute = 0.0005;

    private readonly record struct TurningPoint(int Index, bool IsPeak);

    private readonly record struct Leg(int From, int To, bool IsRise, double RatePerMinute, double StartTemperature, double OnFraction, bool HasGap);

    public static TuningResult Estimate(IReadOnlyList<RoomSeries> rooms, TuningSettings settings) =>
        new(rooms.Select(r => EstimateRoom(r, settings)).ToList());

    private static RoomEstimate EstimateRoom(RoomSeries room, TuningSettings settings)
    {
        var readings = room.Readings;
        var countFrom = settings.CountFrom ?? DateTimeOffset.MinValue;
        var legs = Legs(readings).Where(l => !l.HasGap && readings[l.From].At >= countFrom).ToList();

        var falls = legs.Where(l => !l.IsRise && l.OnFraction <= CoolingOnFraction).ToList();
        var rises = legs.Where(l => l.IsRise && l.OnFraction >= HeatingOnFraction).ToList();

        string? note = null;
        double? cooling = null, heating = null;

        if (falls.Count == 0)
        {
            note = $"no finished fall of {MinSwing}°C or more with the heating off yet";
        }
        else
        {
            var perMinute = -Median(falls.Select(l => l.RatePerMinute));
            if (perMinute < MinRatePerMinute)
            {
                note = "no measurable cooling";
            }
            else
            {
                cooling = perMinute * 60;
            }
        }

        if (rises.Count == 0)
        {
            note ??= $"no finished rise of {MinSwing}°C or more with the heating on yet";
        }
        else
        {
            var perMinute = Median(rises.Select(l => l.RatePerMinute));
            if (perMinute < MinRatePerMinute)
            {
                note ??= "no measurable warming";
            }
            else
            {
                heating = perMinute * 60;
            }
        }

        return new RoomEstimate(room.Key, room.Name, falls.Count, cooling, rises.Count, heating, note);
    }

    /// <summary>The rises and falls between consecutive confirmed turning points of the smoothed readings.</summary>
    private static List<Leg> Legs(IReadOnlyList<RoomReading> readings)
    {
        var smoothed = Smooth(readings);
        var points = DropUnconfirmedStart(smoothed,
            Refine(readings, RemoveShortLegs(readings, smoothed, TurningPoints(smoothed))));

        var legs = new List<Leg>();
        for (var k = 0; k + 1 < points.Count; k++)
        {
            var (from, to) = (points[k].Index, points[k + 1].Index);
            var minutes = (readings[to].At - readings[from].At).TotalMinutes;
            if (minutes <= 0)
            {
                continue;
            }

            var on = 0;
            var gap = false;
            for (var i = from; i <= to; i++)
            {
                on += readings[i].State == HeatingState.On ? 1 : 0;
                gap |= i > from && readings[i].At - readings[i - 1].At > MaxGap;
            }

            var (slope, start) = FitLine(readings, from, to);
            legs.Add(new Leg(
                from, to,
                IsRise: !points[k].IsPeak,
                RatePerMinute: slope,
                StartTemperature: start,
                OnFraction: (double)on / (to - from + 1),
                HasGap: gap));
        }

        return legs;
    }

    /// <summary>Centred median over <see cref="SmoothingWindow"/>, which removes single-reading noise.</summary>
    private static double[] Smooth(IReadOnlyList<RoomReading> readings)
    {
        var half = SmoothingWindow / 2;
        var result = new double[readings.Count];
        for (var i = 0; i < readings.Count; i++)
        {
            var window = new List<double>();
            for (var j = i; j >= 0 && readings[i].At - readings[j].At <= half; j--)
            {
                window.Add(readings[j].AirTemperature);
            }

            for (var j = i + 1; j < readings.Count && readings[j].At - readings[i].At <= half; j++)
            {
                window.Add(readings[j].AirTemperature);
            }

            result[i] = Median(window);
        }

        return result;
    }

    /// <summary>Peaks and troughs, each confirmed by the temperature moving at least <see cref="MinSwing"/> back from it.</summary>
    private static List<TurningPoint> TurningPoints(double[] t)
    {
        var points = new List<TurningPoint>();
        if (t.Length == 0)
        {
            return points;
        }

        bool? rising = null;
        int low = 0, high = 0;   // running extremes until the first direction is known
        var candidate = 0;
        for (var i = 1; i < t.Length; i++)
        {
            switch (rising)
            {
                case null:
                    if (t[i] < t[low]) low = i;
                    if (t[i] > t[high]) high = i;
                    if (t[i] >= t[low] + MinSwing)
                    {
                        points.Add(new TurningPoint(low, IsPeak: false));
                        (rising, candidate) = (true, i);
                    }
                    else if (t[i] <= t[high] - MinSwing)
                    {
                        points.Add(new TurningPoint(high, IsPeak: true));
                        (rising, candidate) = (false, i);
                    }

                    break;

                case true:
                    if (t[i] > t[candidate])
                    {
                        candidate = i;
                    }
                    else if (t[i] <= t[candidate] - MinSwing)
                    {
                        points.Add(new TurningPoint(candidate, IsPeak: true));
                        (rising, candidate) = (false, i);
                    }

                    break;

                case false:
                    if (t[i] < t[candidate])
                    {
                        candidate = i;
                    }
                    else if (t[i] >= t[candidate] + MinSwing)
                    {
                        points.Add(new TurningPoint(candidate, IsPeak: false));
                        (rising, candidate) = (true, i);
                    }

                    break;
            }
        }

        return points;
    }

    /// <summary>
    /// Repeatedly removes the shortest rise or fall while it is shorter than <see cref="MinLeg"/>. Its far end is
    /// dropped together with the less extreme of the two turning points of the same kind either side, so a short
    /// disturbance in the middle of a long fall (or rise) merges into it and the true peak or trough is kept.
    /// </summary>
    private static List<TurningPoint> RemoveShortLegs(IReadOnlyList<RoomReading> readings, double[] t, List<TurningPoint> points)
    {
        while (points.Count >= 2)
        {
            var shortest = -1;
            var shortestLength = MinLeg;
            for (var k = 0; k + 1 < points.Count; k++)
            {
                var length = readings[points[k + 1].Index].At - readings[points[k].Index].At;
                if (length < shortestLength)
                {
                    (shortest, shortestLength) = (k, length);
                }
            }

            if (shortest < 0)
            {
                break;
            }

            // At either end there is nothing to merge with: the short movement touches the edge of the history, so
            // drop its outer point (the start or the end of the history) and keep the real turning point.
            if (shortest == 0)
            {
                points.RemoveAt(0);
                continue;
            }

            if (shortest + 2 >= points.Count)
            {
                points.RemoveAt(shortest + 1);
                continue;
            }

            var (a, c) = (points[shortest], points[shortest + 2]);
            var keepA = a.IsPeak ? t[a.Index] >= t[c.Index] : t[a.Index] <= t[c.Index];
            if (keepA)
            {
                points.RemoveRange(shortest + 1, 2);
            }
            else
            {
                points.RemoveRange(shortest, 2);
            }
        }

        return points;
    }

    /// <summary>
    /// Drops the first turning point unless the readings before it show it really was one: a trough needs a fall of at
    /// least <see cref="MinSwing"/> into it, a peak a rise of at least <see cref="MinSwing"/>. Otherwise it is just where
    /// the history window happens to start (for example part-way up a rise), and the movement from it would be measured
    /// over only part of its length.
    /// </summary>
    private static List<TurningPoint> DropUnconfirmedStart(double[] t, List<TurningPoint> points)
    {
        if (points.Count == 0)
        {
            return points;
        }

        var first = points[0];
        var before = t.Take(first.Index + 1).ToList();
        var confirmed = first.IsPeak
            ? before.Min() <= t[first.Index] - MinSwing
            : before.Max() >= t[first.Index] + MinSwing;
        if (!confirmed)
        {
            points.RemoveAt(0);
        }

        return points;
    }

    /// <summary>
    /// Moves each turning point to the highest (peak) or lowest (trough) raw reading within half the smoothing window,
    /// since smoothing can shift where a peak or trough appears when the rise and fall rates differ.
    /// </summary>
    private static List<TurningPoint> Refine(IReadOnlyList<RoomReading> readings, List<TurningPoint> points)
    {
        var half = SmoothingWindow / 2;
        var refined = new List<TurningPoint>(points.Count);
        for (var k = 0; k < points.Count; k++)
        {
            var p = points[k];
            var lowerLimit = k > 0 ? refined[k - 1].Index + 1 : 0;
            var upperLimit = k + 1 < points.Count ? points[k + 1].Index - 1 : readings.Count - 1;
            var best = p.Index;
            for (var i = p.Index; i >= lowerLimit && readings[p.Index].At - readings[i].At <= half; i--)
            {
                best = Better(i, best);
            }

            for (var i = p.Index + 1; i <= upperLimit && readings[i].At - readings[p.Index].At <= half; i++)
            {
                best = Better(i, best);
            }

            refined.Add(p with { Index = best });

            int Better(int i, int current) =>
                p.IsPeak ? (readings[i].AirTemperature > readings[current].AirTemperature ? i : current)
                         : (readings[i].AirTemperature < readings[current].AirTemperature ? i : current);
        }

        return refined;
    }

    /// <summary>Least-squares line through readings[from..to]: slope in °C per minute and the fitted value at the first reading.</summary>
    private static (double SlopePerMinute, double Start) FitLine(IReadOnlyList<RoomReading> readings, int from, int to)
    {
        var t0 = readings[from].At;
        var n = to - from + 1;
        double sx = 0, sy = 0;
        for (var i = from; i <= to; i++)
        {
            sx += (readings[i].At - t0).TotalMinutes;
            sy += readings[i].AirTemperature;
        }

        var (xMean, yMean) = (sx / n, sy / n);
        double sxx = 0, sxy = 0;
        for (var i = from; i <= to; i++)
        {
            var dx = (readings[i].At - t0).TotalMinutes - xMean;
            sxx += dx * dx;
            sxy += dx * (readings[i].AirTemperature - yMean);
        }

        var slope = sxx == 0 ? 0 : sxy / sxx;
        return (slope, yMean - slope * xMean);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
