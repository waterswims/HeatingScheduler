using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public sealed class OctopusOptions
{
    public const string SectionName = "Octopus";

    /// <summary>Agile product code, e.g. "AGILE-24-10-01". Check your Octopus account for the exact code.</summary>
    [Required]
    public string ProductCode { get; set; } = "";

    /// <summary>Grid supply point region letter (A–P), the last letter of your tariff code.</summary>
    [Required]
    [RegularExpression("^[A-HJ-NP]$", ErrorMessage = "RegionCode must be a single region letter A–P (excluding I and O).")]
    public string RegionCode { get; set; } = "";

    public string TariffCode => $"E-1R-{ProductCode}-{RegionCode}";
}
