using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatingScheduler.Wavin;

internal sealed class FirebaseSignInRequest
{
    [JsonPropertyName("email")] public required string Email { get; init; }
    [JsonPropertyName("password")] public required string Password { get; init; }
    [JsonPropertyName("returnSecureToken")] public bool ReturnSecureToken { get; init; } = true;
    [JsonPropertyName("clientType")] public string ClientType { get; init; } = "CLIENT_TYPE_WEB";
}

internal sealed class FirebaseSignInResponse
{
    [JsonPropertyName("idToken")] public string IdToken { get; set; } = "";
    [JsonPropertyName("expiresIn")] public string? ExpiresIn { get; set; }
}

public sealed class ListDevicesResponse
{
    [JsonPropertyName("devices")] public List<WavinDevice> Devices { get; set; } = [];
}

public sealed class WavinDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
    [JsonPropertyName("lastConfig")] public WavinLastConfig? LastConfig { get; set; }

    /// <summary>Present only on devices that have not been migrated to the current Wavin platform; those are not supported.</summary>
    [JsonPropertyName("platform")] public JsonElement Platform { get; set; }

    [JsonIgnore] public bool IsMigrated => Platform.ValueKind == JsonValueKind.Undefined;

    [JsonIgnore] public IReadOnlyList<WavinRoom> Rooms => LastConfig?.Sentio?.Rooms ?? [];
}

public sealed class WavinLastConfig
{
    [JsonPropertyName("sentio")] public WavinSentioConfig? Sentio { get; set; }
}

public sealed class WavinSentioConfig
{
    [JsonPropertyName("rooms")] public List<WavinRoom> Rooms { get; set; } = [];
}

public sealed class WavinRoom
{
    /// <summary>Kept as raw JSON so it is echoed back in exactly the form the API sent (number or string).</summary>
    [JsonPropertyName("id")] public JsonElement Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("titlePersonalized")] public string? TitlePersonalized { get; set; }
    [JsonPropertyName("setpointTemperature")] public double? SetpointTemperature { get; set; }
    [JsonPropertyName("airTemperature")] public double? AirTemperature { get; set; }
    [JsonPropertyName("vacationMode")] public string? VacationMode { get; set; }

    [JsonIgnore] public string IdText => Id.ValueKind == JsonValueKind.String ? Id.GetString() ?? "" : Id.GetRawText();
    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(TitlePersonalized) ? TitlePersonalized : Title ?? IdText;
    [JsonIgnore] public bool IsInVacationMode => VacationMode == "VACATION_MODE_ON";
}

public sealed record RoomSetpoint(JsonElement RoomId, double SetpointTemperature);

internal sealed class SendDeviceConfigRequest
{
    [JsonPropertyName("device_name")] public required string DeviceName { get; init; }
    [JsonPropertyName("config")] public required DeviceConfig Config { get; init; }

    internal sealed class DeviceConfig
    {
        [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
        [JsonPropertyName("sentio")] public required SentioConfig Sentio { get; init; }
    }

    internal sealed class SentioConfig
    {
        [JsonPropertyName("rooms")] public required List<RoomConfig> Rooms { get; init; }
    }

    internal sealed class RoomConfig
    {
        [JsonPropertyName("id")] public required JsonElement Id { get; init; }
        [JsonPropertyName("setpointTemperature")] public required double SetpointTemperature { get; init; }
    }
}
