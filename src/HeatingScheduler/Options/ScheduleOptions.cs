using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public enum HeatingState
{
    Off,
    On,
}

public sealed class ScheduleOptions : IValidatableObject
{
    public const string SectionName = "Schedule";

    /// <summary>
    /// After an on-block, the heating may be off for at most this ratio × the length of that block
    /// (e.g. 2.0: one hour on allows up to two hours off). It may be off for less.
    /// </summary>
    [Range(0.1, 48.0)]
    public double OffToOnRatio { get; set; } = 1.0;

    /// <summary>Shortest on-block, in minutes (a multiple of 30). 30 means no minimum.</summary>
    [Range(30, 1440)]
    public int MinOnMinutes { get; set; } = 60;

    /// <summary>
    /// After a restart, assume the heating has just finished an on-block this long (a multiple of 30, in minutes).
    /// 0 means the heating must come on straight away.
    /// </summary>
    [Range(0, 1440)]
    public int StartupAssumedOnMinutes { get; set; } = 60;

    /// <summary>How far ahead (rolling) the solver plans, limited by how far ahead prices are published.</summary>
    [Range(1, 48)]
    public int HorizonHours { get; set; } = 24;

    /// <summary>State used when there is no price for the current slot (e.g. prices could not be fetched).</summary>
    public HeatingState FallbackState { get; set; } = HeatingState.On;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinOnMinutes % 30 != 0)
        {
            yield return new ValidationResult("MinOnMinutes must be a multiple of 30.", [nameof(MinOnMinutes)]);
        }

        if (StartupAssumedOnMinutes % 30 != 0)
        {
            yield return new ValidationResult("StartupAssumedOnMinutes must be a multiple of 30.", [nameof(StartupAssumedOnMinutes)]);
        }
    }
}
