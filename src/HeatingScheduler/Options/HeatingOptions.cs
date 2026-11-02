using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public sealed class HeatingOptions : IValidatableObject
{
    public const string SectionName = "Heating";

    [Range(5.0, 35.0)]
    public double OnTemperature { get; set; } = 21.0;

    [Range(5.0, 35.0)]
    public double OffTemperature { get; set; } = 17.0;

    /// <summary>Longest time between checks of the Wavin setpoints. Checks also happen at every half-hour slot boundary.</summary>
    [Range(1, 60)]
    public int MaxCheckIntervalMinutes { get; set; } = 5;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OffTemperature >= OnTemperature)
        {
            yield return new ValidationResult(
                "OffTemperature must be lower than OnTemperature.",
                [nameof(OffTemperature), nameof(OnTemperature)]);
        }
    }

    public double TemperatureFor(HeatingState state) => state == HeatingState.On ? OnTemperature : OffTemperature;
}
