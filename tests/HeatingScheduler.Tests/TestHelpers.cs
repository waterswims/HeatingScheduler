using System.Net;
using HeatingScheduler.Octopus;
using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

internal static class TestHelpers
{
    public static IOptionsWrapper<T> Opt<T>(T value) where T : class => new(value);

    /// <summary>Creates <paramref name="count"/> consecutive priced slots from <paramref name="start"/> using <paramref name="price"/>(index).</summary>
    public static List<AgileSlot> SlotsFrom(DateTimeOffset start, int count, Func<int, decimal> price) =>
        Enumerable.Range(0, count)
            .Select(i => new AgileSlot(start + i * Slots.Length, start + (i + 1) * Slots.Length, price(i)))
            .ToList();
}

internal sealed class IOptionsWrapper<T>(T value) : Microsoft.Extensions.Options.IOptions<T> where T : class
{
    public T Value { get; } = value;
}

/// <summary>HttpMessageHandler that returns canned responses and records requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
