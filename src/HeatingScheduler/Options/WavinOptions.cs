using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public sealed class WavinOptions
{
    public const string SectionName = "Wavin";

    [Required]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    /// <summary>Public Firebase web API key used by the Wavin Sentio app.</summary>
    [Required]
    public string FirebaseApiKey { get; set; } = "AIzaSyBlAtNI7-2jitPul9I-O4EZcT-n0sIay-g";

    [Required]
    public Uri AuthBaseUrl { get; set; } = new("https://identitytoolkit.googleapis.com/");

    [Required]
    public Uri DeviceServiceBaseUrl { get; set; } = new("https://blaze.wavinsentio.com/");

    /// <summary>Optional device name (e.g. "devices/1234"). When empty, all migrated devices on the account are used.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Optional list of room ids or titles to control. When empty, all rooms are controlled.</summary>
    public List<string> Rooms { get; set; } = [];
}
