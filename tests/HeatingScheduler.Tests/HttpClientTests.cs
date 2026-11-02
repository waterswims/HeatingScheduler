using System.Net;
using System.Text.Json;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeatingScheduler.Tests;

public class OctopusAgileClientTests
{
    [Fact]
    public async Task Follows_pagination_and_returns_sorted_slots()
    {
        const string page1 = """
            {"count":3,"next":"https://api.octopus.energy/page2","previous":null,"results":[
              {"value_exc_vat":10.0,"value_inc_vat":10.5,"valid_from":"2026-11-02T23:30:00Z","valid_to":"2026-11-03T00:00:00Z","payment_method":null},
              {"value_exc_vat":-1.0,"value_inc_vat":-1.05,"valid_from":"2026-11-03T00:00:00Z","valid_to":"2026-11-03T00:30:00Z","payment_method":null}]}
            """;
        const string page2 = """
            {"count":3,"next":null,"previous":null,"results":[
              {"value_exc_vat":20.0,"value_inc_vat":21.0,"valid_from":"2026-11-02T23:00:00Z","valid_to":"2026-11-02T23:30:00Z","payment_method":null}]}
            """;
        var handler = new StubHandler(req => StubHandler.Json(req.RequestUri!.AbsolutePath == "/page2" ? page2 : page1));
        var client = new OctopusAgileClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.octopus.energy/") },
            TestHelpers.Opt(new OctopusOptions { ProductCode = "AGILE-24-10-01", RegionCode = "C" }));

        var slots = await client.GetRatesAsync(
            DateTimeOffset.Parse("2026-11-02T23:00:00Z"), DateTimeOffset.Parse("2026-11-03T23:00:00Z"), CancellationToken.None);

        Assert.Equal(
            "https://api.octopus.energy/v1/products/AGILE-24-10-01/electricity-tariffs/E-1R-AGILE-24-10-01-C/standard-unit-rates/" +
            "?period_from=2026-11-02T23:00:00Z&period_to=2026-11-03T23:00:00Z&page_size=1500",
            handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([21.0m, 10.5m, -1.05m], slots.Select(s => s.PriceIncVat));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T23:00:00Z"), slots[0].ValidFrom);
    }
}

public class WavinClientTests
{
    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private const string DeviceJson = """
        {"name":"devices/abc","serialNumber":"123","lastConfig":{"sentio":{"standbyMode":"STANDBY_MODE_OFF","rooms":[
          {"id":"7","title":"Room 1","titlePersonalized":"Lounge","setpointTemperature":20.5,"airTemperature":19.8,"vacationMode":"VACATION_MODE_OFF"}]}}}
        """;

    private static (WavinClient Client, StubHandler Api, StubHandler Auth) Create(Func<HttpRequestMessage, int, HttpResponseMessage>? api = null)
    {
        var authHandler = new StubHandler(_ => StubHandler.Json("""{"idToken":"token-1","refreshToken":"r","expiresIn":"3600"}"""));
        var options = TestHelpers.Opt(new WavinOptions { Email = "me@example.com", Password = "pw" });
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00:00Z"));
        var authenticator = new WavinAuthenticator(
            new SingleClientFactory(new HttpClient(authHandler)), options, time, NullLogger<WavinAuthenticator>.Instance);

        var calls = 0;
        var apiHandler = new StubHandler(req => api?.Invoke(req, ++calls) ?? StubHandler.Json(DeviceJson));
        var http = new HttpClient(new WavinAuthHandler(authenticator) { InnerHandler = apiHandler })
        {
            BaseAddress = new Uri("https://blaze.wavinsentio.com/"),
        };
        return (new WavinClient(http, time), apiHandler, authHandler);
    }

    [Fact]
    public async Task GetDevice_parses_rooms_and_sends_bearer_token()
    {
        var (client, api, auth) = Create();

        var device = await client.GetDeviceAsync("devices/abc", CancellationToken.None);

        var room = Assert.Single(device.Rooms);
        Assert.Equal("7", room.IdText);
        Assert.Equal("Lounge", room.DisplayName);
        Assert.Equal(20.5, room.SetpointTemperature);
        Assert.True(device.IsMigrated);

        var (request, body) = Assert.Single(api.Requests);
        Assert.Equal("https://blaze.wavinsentio.com/wavin.blaze.v1.BlazeDeviceService/GetDevice", request.RequestUri!.ToString());
        Assert.Equal("Bearer token-1", request.Headers.Authorization!.ToString());
        Assert.Equal("""{"name":"devices/abc"}""", body);

        var (authRequest, authBody) = Assert.Single(auth.Requests);
        Assert.StartsWith("https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=", authRequest.RequestUri!.ToString());
        using var authJson = JsonDocument.Parse(authBody!);
        Assert.Equal("me@example.com", authJson.RootElement.GetProperty("email").GetString());
        Assert.True(authJson.RootElement.GetProperty("returnSecureToken").GetBoolean());
    }

    [Fact]
    public async Task SetRoomSetpoints_sends_device_config_echoing_room_id()
    {
        var (client, api, _) = Create();
        var device = await client.GetDeviceAsync("devices/abc", CancellationToken.None);

        await client.SetRoomSetpointsAsync(
            "devices/abc", [new RoomSetpoint(device.Rooms[0].Id, 21.0)], CancellationToken.None);

        var (request, body) = api.Requests[1];
        Assert.EndsWith("/wavin.blaze.v1.BlazeDeviceService/SendDeviceConfig", request.RequestUri!.ToString());
        Assert.Equal(
            """{"device_name":"devices/abc","config":{"timestamp":"2026-11-02T12:00:00.000000Z","sentio":{"rooms":[{"id":"7","setpointTemperature":21}]}}}""",
            body);
    }

    [Fact]
    public async Task Signs_in_again_and_retries_once_on_401()
    {
        var (client, api, auth) = Create((_, call) =>
            call == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : StubHandler.Json(DeviceJson));

        var device = await client.GetDeviceAsync("devices/abc", CancellationToken.None);

        Assert.Equal("devices/abc", device.Name);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(2, auth.Requests.Count);
    }
}
