namespace HeatingScheduler.Scheduling;

/// <summary>
/// An Agile pricing day: 23:00 UK local time on <see cref="StartDate"/> to 23:00 UK local time the next day.
/// Usually 48 half-hour slots; 46 or 50 on clock-change days.
/// </summary>
public readonly record struct AgileDay(DateOnly StartDate)
{
    public static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(30);
    private static readonly TimeOnly DayBoundary = new(23, 0);

    public static TimeZoneInfo UkTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public DateTimeOffset StartUtc => ToUtc(StartDate);
    public DateTimeOffset EndUtc => ToUtc(StartDate.AddDays(1));
    public int SlotCount => (int)((EndUtc - StartUtc) / SlotLength);

    public AgileDay Next => new(StartDate.AddDays(1));

    public static AgileDay Containing(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, UkTimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return TimeOnly.FromDateTime(local.DateTime) >= DayBoundary ? new AgileDay(date) : new AgileDay(date.AddDays(-1));
    }

    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;

    public IEnumerable<DateTimeOffset> SlotStarts()
    {
        for (var t = StartUtc; t < EndUtc; t += SlotLength)
        {
            yield return t;
        }
    }

    /// <summary>Start of the half-hour slot containing <paramref name="instant"/> (UTC). UK offsets are whole hours, so UTC half-hours align with Agile slots.</summary>
    public static DateTimeOffset SlotStartFor(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % SlotLength.Ticks, TimeSpan.Zero);
    }

    public override string ToString() =>
        $"Agile day {StartDate:yyyy-MM-dd} 23:00 → {StartDate.AddDays(1):yyyy-MM-dd} 23:00 UK";

    // 23:00 is never skipped or repeated by UK clock changes, so the conversion is unambiguous.
    private static DateTimeOffset ToUtc(DateOnly localDate) =>
        new(TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(DayBoundary, DateTimeKind.Unspecified), UkTimeZone), TimeSpan.Zero);
}
