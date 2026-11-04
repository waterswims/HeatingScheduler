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
    /// After an off period, the heating must be on for at least this ratio × the length of that off period
    /// (rounded up to whole slots) to make up the heat lost. E.g. 0.5: two hours off requires at least one hour on.
    /// </summary>
    [Range(0.01, 10.0)]
    public double OnToOffRatio { get; set; } = 0.25;

    /// <summary>Longest allowed off period, in minutes (a multiple of 30), to limit heat loss.</summary>
    [Range(30, 1440)]
    public int MaxOffMinutes { get; set; } = 180;

    /// <summary>Shortest on-block, in minutes (a multiple of 30). 30 means no minimum beyond the make-up time.</summary>
    [Range(30, 1440)]
    public int MinOnMinutes { get; set; } = 60;

    /// <summary>
    /// After a restart, assume the heating has been on for this long (a multiple of 30, in minutes) with no heat left
    /// to make up. If this is less than <see cref="MinOnMinutes"/>, the heating stays on until the minimum is reached.
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
        foreach (var (name, value) in new[]
                 {
                     (nameof(MaxOffMinutes), MaxOffMinutes),
                     (nameof(MinOnMinutes), MinOnMinutes),
                     (nameof(StartupAssumedOnMinutes), StartupAssumedOnMinutes),
                 })
        {
            if (value % 30 != 0)
            {
                yield return new ValidationResult($"{name} must be a multiple of 30.", [name]);
            }
        }
    }
}
