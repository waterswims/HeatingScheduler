using System.Globalization;

namespace HeatingScheduler.Scheduling;

/// <summary>Helpers for Agile's half-hour price slots.</summary>
public static class Slots
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(30);

    public static TimeZoneInfo UkTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    /// <summary>Start of the half-hour slot containing <paramref name="instant"/> (UTC). UK offsets are whole hours, so UTC half-hours align with Agile slots.</summary>
    public static DateTimeOffset StartFor(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % Length.Ticks, TimeSpan.Zero);
    }

    public static int FromMinutes(int minutes) => minutes / (int)Length.TotalMinutes;

    /// <summary>Formats an instant as UK local time, e.g. "Tue 22:30".</summary>
    public static string Uk(DateTimeOffset instant, string format = "ddd HH:mm") =>
        TimeZoneInfo.ConvertTime(instant, UkTimeZone).ToString(format, CultureInfo.InvariantCulture);
}
