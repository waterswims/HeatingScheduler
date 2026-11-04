using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

/// <summary>
/// Settings for working out <see cref="ScheduleOptions.OnToOffRatio"/> and <see cref="ScheduleOptions.MaxOffMinutes"/>
/// from the Sentio's room temperature history.
/// </summary>
public sealed class TuningOptions
{
    public const string SectionName = "Tuning";

    /// <summary>When false, the values in <see cref="ScheduleOptions"/> are used unchanged.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Air temperature (°C) every room should stay above during an off period.</summary>
    [Range(5.0, 35.0)]
    public double MinTemperature { get; set; } = 21.0;

    /// <summary>
    /// Room ids or titles to tune against, e.g. a room away from windows, ovens and showers whose temperature
    /// mostly reflects the heating. When empty, every controlled room is used and the most demanding one decides.
    /// </summary>
    public List<string> Rooms { get; set; } = [];
}
