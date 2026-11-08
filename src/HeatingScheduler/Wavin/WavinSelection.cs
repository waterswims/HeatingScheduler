namespace HeatingScheduler.Wavin;

/// <summary>Which Wavin devices and rooms the app works with.</summary>
public static class WavinSelection
{
    /// <summary>Every device on the account that has been migrated to the current Wavin platform.</summary>
    public static async Task<IReadOnlyList<WavinDevice>> GetDevicesAsync(this IWavinClient wavin, CancellationToken cancellationToken)
    {
        var names = (await wavin.ListDevicesAsync(cancellationToken)).Where(d => d.IsMigrated).Select(d => d.Name).ToList();
        var devices = new List<WavinDevice>(names.Count);
        foreach (var name in names)
        {
            devices.Add(await wavin.GetDeviceAsync(name, cancellationToken));
        }

        return devices;
    }

    /// <summary>The rooms matching <paramref name="filter"/> (by id or title), or all rooms when the list is empty.</summary>
    public static IEnumerable<WavinRoom> SelectRooms(IReadOnlyList<WavinRoom> rooms, IReadOnlyList<string> filter) =>
        filter.Count == 0 ? rooms : rooms.Where(r => filter.Any(e => Matches(r, e)));

    /// <summary>
    /// The rooms whose temperature the plan tracks: controlled rooms (<paramref name="controlFilter"/>) not in vacation
    /// mode, narrowed to <paramref name="trackFilter"/> when it is set. Keyed by device and room id.
    /// </summary>
    public static IEnumerable<(string Key, WavinRoom Room)> TrackedRooms(
        IReadOnlyList<WavinDevice> devices, IReadOnlyList<string> controlFilter, IReadOnlyList<string> trackFilter) =>
        devices.SelectMany(d => SelectRooms(SelectRooms(d.Rooms, controlFilter).Where(r => !r.IsInVacationMode).ToList(), trackFilter)
            .Select(r => (Key: $"{d.Name}/{r.IdText}", Room: r)));

    public static bool Matches(WavinRoom room, string entry) =>
        string.Equals(room.IdText, entry, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(room.Title, entry, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(room.TitlePersonalized, entry, StringComparison.OrdinalIgnoreCase);
}
