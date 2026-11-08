using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public enum HeatingState
{
    Off,
    On,
}

/// <summary>How the plan is made: keep the tracked rooms above a minimum temperature as cheaply as possible.</summary>
public sealed class ScheduleOptions : IValidatableObject
{
    public const string SectionName = "Schedule";

    /// <summary>Air temperature (°C) every tracked room should stay above.</summary>
    [Range(5.0, 35.0)]
    public double MinTemperature { get; set; } = 20.5;

    /// <summary>How far above <see cref="MinTemperature"/> the plan keeps the predicted temperature, to allow for model error (°C).</summary>
    [Range(0.0, 3.0)]
    public double SafetyMargin { get; set; } = 0.2;

    /// <summary>
    /// Room ids or titles whose temperature the plan tracks (and that tuning measures), e.g. a room away from windows,
    /// ovens and showers. When empty, every controlled room is tracked and all of them must stay above the minimum.
    /// </summary>
    public List<string> Rooms { get; set; } = [];

    /// <summary>Cooling rate (°C per hour, heating off) used until tuning has measured it, or always if tuning is off.</summary>
    [Range(0.01, 5.0)]
    public double CoolingRate { get; set; } = 0.15;

    /// <summary>Heating rate (°C per hour, heating on) used until tuning has measured it, or always if tuning is off.</summary>
    [Range(0.01, 5.0)]
    public double HeatingRate { get; set; } = 0.15;

    /// <summary>Shortest on-block, in minutes (a multiple of 30). 30 means no minimum.</summary>
    [Range(30, 1440)]
    public int MinOnMinutes { get; set; } = 60;

    /// <summary>State used when there is no plan for the current slot (no prices, or no recent room temperature).</summary>
    public HeatingState FallbackState { get; set; } = HeatingState.On;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinOnMinutes % 30 != 0)
        {
            yield return new ValidationResult("MinOnMinutes must be a multiple of 30.", [nameof(MinOnMinutes)]);
        }
    }

    /// <summary>The lowest temperature the plan lets a tracked room be predicted to reach.</summary>
    public double Floor => MinTemperature + SafetyMargin;
}
