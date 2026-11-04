using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HeatingScheduler.Wavin;

public interface IWavinClient
{
    Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken);
    Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken);
    Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken);

    /// <summary>Historical readings for one room, aggregated into <paramref name="bucketMinutes"/>-minute buckets.</summary>
    Task<IReadOnlyList<RoomTimeSeriesRow>> GetRoomTimeSeriesAsync(
        string deviceName, string roomId, DateTimeOffset start, DateTimeOffset end, int bucketMinutes, CancellationToken cancellationToken);
}

/// <summary>
/// Client for the (unofficial) Wavin Sentio "Blaze" device service, as used by the Wavin Sentio app.
/// The HttpClient's base address must be <see cref="BaseAddress"/>; authentication is added by <see cref="WavinAuthHandler"/>.
/// </summary>
public sealed class WavinClient(HttpClient httpClient, TimeProvider timeProvider) : IWavinClient
{
    public static readonly Uri BaseAddress = new("https://blaze.wavinsentio.com/");

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

    /// <summary>
    /// Calls the server-streaming <c>GetSentioRoomTimeSeries</c> method using the Connect protocol: the request and each
    /// response message are framed as [flags (1 byte)][length (4 bytes, big-endian)][JSON]. A frame with flag 0x02 ends the
    /// stream and may carry an error.
    /// </summary>
    public async Task<IReadOnlyList<RoomTimeSeriesRow>> GetRoomTimeSeriesAsync(
        string deviceName, string roomId, DateTimeOffset start, DateTimeOffset end, int bucketMinutes, CancellationToken cancellationToken)
    {
        const string method = "GetSentioRoomTimeSeries";
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            deviceName,
            roomId,
            startTime = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            endTime = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            bucketIntervalMinutes = bucketMinutes.ToString(CultureInfo.InvariantCulture),
        });

        var body = new byte[5 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), json.Length);
        json.CopyTo(body, 5);

        using var request = new HttpRequestMessage(HttpMethod.Post, ServicePath + method) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+json");
        request.Headers.Add("Connect-Protocol-Version", "1");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Wavin {method} failed with {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}", null, response.StatusCode);
        }

        var rows = new List<RoomTimeSeriesRow>();
        var i = 0;
        while (i < bytes.Length)
        {
            if (i + 5 > bytes.Length)
            {
                throw new HttpRequestException($"Wavin {method} returned a truncated stream (incomplete frame header).");
            }

            var flags = bytes[i];
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i + 1, 4));
            if (length < 0 || length > bytes.Length - i - 5)
            {
                throw new HttpRequestException(
                    $"Wavin {method} returned a truncated stream (frame of {length} bytes, {bytes.Length - i - 5} available).");
            }

            if ((flags & 0x01) != 0)
            {
                // Compression is only used when the client offers it (Connect-Accept-Encoding), which this client doesn't.
                throw new HttpRequestException($"Wavin {method} returned a compressed frame, which is not supported.");
            }

            var frame = bytes.AsSpan(i + 5, length);
            i += 5 + length;

            if ((flags & 0x02) != 0)
            {
                using var endOfStream = JsonDocument.Parse(frame.ToArray());
                if (endOfStream.RootElement.TryGetProperty("error", out var error))
                {
                    throw new HttpRequestException($"Wavin {method} failed: {error.GetRawText()}");
                }

                break;
            }

            rows.Add(JsonSerializer.Deserialize<RoomTimeSeriesRow>(frame, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Wavin {method} returned an empty row."));
        }

        return rows;
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
