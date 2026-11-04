using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

public sealed class WavinOptions
{
    public const string SectionName = "Wavin";

    [Required]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    /// <summary>Optional list of room ids or titles to control. When empty, all rooms are controlled.</summary>
    public List<string> Rooms { get; set; } = [];
}
