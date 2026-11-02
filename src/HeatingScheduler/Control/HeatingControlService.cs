using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Control;

/// <summary>
/// Service 2: checks the Wavin Sentio room setpoints and sets them to the on/off temperature
/// required by the current slot of the <see cref="HeatingSchedule"/>.
/// </summary>
public sealed class HeatingControlService(
    IWavinClient wavin,
    HeatingSchedule schedule,
    IOptions<WavinOptions> wavinOptions,
    IOptions<HeatingOptions> heatingOptions,
    TimeProvider timeProvider,
    ILogger<HeatingControlService> logger) : BackgroundService
{
    private const double Tolerance = 0.05;
    private static readonly TimeSpan SlotBoundaryDelay = TimeSpan.FromSeconds(15);

    private readonly HashSet<string> _warnedUnmatchedRooms = new(StringComparer.OrdinalIgnoreCase);
    private DesiredState? _lastDesired;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schedule.WaitForFirstAttemptAsync(TimeSpan.FromMinutes(2), timeProvider, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to check or update Wavin setpoints; will retry");
            }

            try
            {
                await Task.Delay(NextDelay(timeProvider.GetUtcNow()), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Wait until just after the next half-hour slot boundary, but never longer than MaxCheckIntervalMinutes.</summary>
    public TimeSpan NextDelay(DateTimeOffset now)
    {
        var untilNextSlot = AgileDay.SlotStartFor(now) + AgileDay.SlotLength + SlotBoundaryDelay - now;
        var max = TimeSpan.FromMinutes(heatingOptions.Value.MaxCheckIntervalMinutes);
        return untilNextSlot < max ? untilNextSlot : max;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var desired = schedule.GetDesiredState(now);
        var target = heatingOptions.Value.TemperatureFor(desired.State);

        if (desired != _lastDesired)
        {
            if (desired.IsFallback)
            {
                logger.LogWarning("No price schedule for the current slot; using fallback state {State} ({Target}°C)", desired.State, target);
            }
            else
            {
                logger.LogInformation("Heating should be {State} ({Target}°C) for slot starting {Slot:u}", desired.State, target, AgileDay.SlotStartFor(now));
            }

            _lastDesired = desired;
        }

        foreach (var device in await GetDevicesAsync(cancellationToken))
        {
            var changes = new List<RoomSetpoint>();
            foreach (var room in SelectRooms(device.Rooms))
            {
                if (room.IsInVacationMode)
                {
                    logger.LogDebug("Skipping room {Room} on {Device}: vacation mode is on", room.DisplayName, device.Name);
                    continue;
                }

                if (room.SetpointTemperature is { } current && Math.Abs(current - target) <= Tolerance)
                {
                    continue;
                }

                logger.LogInformation(
                    "Setting room {Room} on {Device} from {Current}°C to {Target}°C (heating {State})",
                    room.DisplayName, device.Name, room.SetpointTemperature, target, desired.State);
                changes.Add(new RoomSetpoint(room.Id, target));
            }

            if (changes.Count > 0)
            {
                await wavin.SetRoomSetpointsAsync(device.Name, changes, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<WavinDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var names = string.IsNullOrWhiteSpace(wavinOptions.Value.DeviceName)
            ? (await wavin.ListDevicesAsync(cancellationToken)).Where(d => d.IsMigrated).Select(d => d.Name).ToList()
            : [wavinOptions.Value.DeviceName];

        if (names.Count == 0)
        {
            logger.LogWarning("No supported Wavin Sentio devices found on the account");
        }

        var devices = new List<WavinDevice>(names.Count);
        foreach (var name in names)
        {
            devices.Add(await wavin.GetDeviceAsync(name, cancellationToken));
        }

        return devices;
    }

    private IEnumerable<WavinRoom> SelectRooms(IReadOnlyList<WavinRoom> rooms)
    {
        var filter = wavinOptions.Value.Rooms;
        if (filter.Count == 0)
        {
            return rooms;
        }

        bool Matches(WavinRoom room, string entry) =>
            string.Equals(room.IdText, entry, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(room.Title, entry, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(room.TitlePersonalized, entry, StringComparison.OrdinalIgnoreCase);

        foreach (var entry in filter.Where(e => !rooms.Any(r => Matches(r, e))))
        {
            if (_warnedUnmatchedRooms.Add(entry))
            {
                logger.LogWarning(
                    "Configured room '{Room}' was not found. Available rooms: {Rooms}",
                    entry, string.Join(", ", rooms.Select(r => $"{r.DisplayName} (id {r.IdText})")));
            }
        }

        return rooms.Where(r => filter.Any(e => Matches(r, e)));
    }
}
