namespace HeatingScheduler.Options;

/// <summary>
/// Measuring each tracked room's cooling and heating rates from the Sentio's temperature history, in place of
/// <see cref="ScheduleOptions.CoolingRate"/> and <see cref="ScheduleOptions.HeatingRate"/>.
/// </summary>
public sealed class TuningOptions
{
    public const string SectionName = "Tuning";

    /// <summary>When false, the rates in <see cref="ScheduleOptions"/> are used unchanged.</summary>
    public bool Enabled { get; set; } = true;
}
