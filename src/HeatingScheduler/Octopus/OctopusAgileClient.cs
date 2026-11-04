using System.Globalization;
using System.Net.Http.Json;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Octopus;

public interface IOctopusAgileClient
{
    Task<IReadOnlyList<AgileSlot>> GetRatesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}

public sealed class OctopusAgileClient(HttpClient httpClient, IOptions<OctopusOptions> options) : IOctopusAgileClient
{
    public static readonly Uri BaseAddress = new("https://api.octopus.energy/");

    private const int MaxPages = 20;

    public async Task<IReadOnlyList<AgileSlot>> GetRatesAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var from = fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var to = toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string? url =
            $"v1/products/{Uri.EscapeDataString(o.ProductCode)}/electricity-tariffs/{Uri.EscapeDataString(o.TariffCode)}" +
            $"/standard-unit-rates/?period_from={from}&period_to={to}&page_size=1500";

        var slots = new Dictionary<DateTimeOffset, AgileSlot>();
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            var response = await httpClient.GetFromJsonAsync<UnitRatesResponse>(url, cancellationToken)
                ?? throw new InvalidOperationException("Octopus API returned an empty response.");

            foreach (var rate in response.Results)
            {
                var validFrom = rate.ValidFrom.ToUniversalTime();
                var validTo = (rate.ValidTo ?? rate.ValidFrom.AddMinutes(30)).ToUniversalTime();
                slots[validFrom] = new AgileSlot(validFrom, validTo, rate.ValueIncVat);
            }

            url = response.Next;
        }

        return slots.Values.OrderBy(s => s.ValidFrom).ToList();
    }
}
