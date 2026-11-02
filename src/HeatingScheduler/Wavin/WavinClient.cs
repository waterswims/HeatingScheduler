using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HeatingScheduler.Wavin;

public interface IWavinClient
{
    Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken);
    Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken);
    Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken);
}

/// <summary>
/// Client for the (unofficial) Wavin Sentio "Blaze" device service, as used by the Wavin Sentio app.
/// Base address must be set to the device service host; authentication is added by <see cref="WavinAuthHandler"/>.
/// </summary>
public sealed class WavinClient(HttpClient httpClient, TimeProvider timeProvider) : IWavinClient
{
    private const string ServicePath = "wavin.blaze.v1.BlazeDeviceService/";

    public async Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var response = await PostAsync<ListDevicesResponse>("ListDevices", new { }, cancellationToken);
        return response.Devices;
    }

    public Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken) =>
        PostAsync<WavinDevice>("GetDevice", new { name = deviceName }, cancellationToken);

    public async Task SetRoomSetpointsAsync(
        string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken)
    {
        if (setpoints.Count == 0)
        {
            return;
        }

        var body = new SendDeviceConfigRequest
        {
            DeviceName = deviceName,
            Config = new SendDeviceConfigRequest.DeviceConfig
            {
                Timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
                Sentio = new SendDeviceConfigRequest.SentioConfig
                {
                    Rooms = setpoints
                        .Select(s => new SendDeviceConfigRequest.RoomConfig { Id = s.RoomId, SetpointTemperature = s.SetpointTemperature })
                        .ToList(),
                },
            },
        };

        using var response = await SendAsync("SendDeviceConfig", body, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string method, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, body, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException($"Wavin {method} returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(string method, object body, CancellationToken cancellationToken)
    {
        // Buffered content so the auth handler can safely resend after a 401.
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, ServicePath + method)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException(
                $"Wavin {method} failed with {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        return response;
    }
}
