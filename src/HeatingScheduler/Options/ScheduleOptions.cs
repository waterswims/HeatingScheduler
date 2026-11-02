using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public enum HeatingState
{
    Off,
    On,
}

public sealed class ScheduleOptions
{
    public const string SectionName = "Schedule";

    /// <summary>Percentage (0–100) of each Agile day's half-hour slots during which heating is on.</summary>
    [Range(0.0, 100.0)]
    public double OnPercentage { get; set; } = 40;

    /// <summary>State used when no schedule is available for the current time (e.g. prices could not be fetched).</summary>
    public HeatingState FallbackState { get; set; } = HeatingState.On;
}
