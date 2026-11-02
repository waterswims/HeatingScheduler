using System.Text.Json.Serialization;

namespace HeatingScheduler.Octopus;

/// <summary>A single half-hour Agile price slot.</summary>
public sealed record AgileSlot(DateTimeOffset ValidFrom, DateTimeOffset ValidTo, decimal PriceIncVat);

internal sealed class UnitRatesResponse
{
    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("results")]
    public List<UnitRateDto> Results { get; set; } = [];
}

internal sealed class UnitRateDto
{
    [JsonPropertyName("value_inc_vat")]
    public decimal ValueIncVat { get; set; }

    [JsonPropertyName("valid_from")]
    public DateTimeOffset ValidFrom { get; set; }

    [JsonPropertyName("valid_to")]
    public DateTimeOffset? ValidTo { get; set; }
}
