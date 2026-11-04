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
    IHeatingSchedule schedule,
    IOptions<WavinOptions> wavinOptions,
    IOptions<HeatingOptions> heatingOptions,
    TimeProvider timeProvider,
    ILogger<HeatingControlService> logger) : BackgroundService
{
    private const double Tolerance = 0.05;
    private static readonly TimeSpan SlotBoundaryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Longest time between checks, so changes made in the Wavin app are put back promptly.</summary>
    public static readonly TimeSpan MaxCheckInterval = TimeSpan.FromMinutes(5);

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

    /// <summary>Wait until just after the next half-hour slot boundary, but never longer than <see cref="MaxCheckInterval"/>.</summary>
    public TimeSpan NextDelay(DateTimeOffset now)
    {
        var untilNextSlot = Slots.StartFor(now) + Slots.Length + SlotBoundaryDelay - now;
        return untilNextSlot < MaxCheckInterval ? untilNextSlot : MaxCheckInterval;
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
                logger.LogInformation("Heating should be {State} ({Target}°C) for slot starting {Slot:u}", desired.State, target, Slots.StartFor(now));
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
        var devices = await wavin.GetDevicesAsync(cancellationToken);
        if (devices.Count == 0)
        {
            logger.LogWarning("No supported Wavin Sentio devices found on the account");
        }

        return devices;
    }

    private IEnumerable<WavinRoom> SelectRooms(IReadOnlyList<WavinRoom> rooms)
    {
        var filter = wavinOptions.Value.Rooms;
        foreach (var entry in filter.Where(e => !rooms.Any(r => WavinSelection.Matches(r, e))))
        {
            if (_warnedUnmatchedRooms.Add(entry))
            {
                logger.LogWarning(
                    "Configured room '{Room}' was not found. Available rooms: {Rooms}",
                    entry, string.Join(", ", rooms.Select(r => $"{r.DisplayName} (id {r.IdText})")));
            }
        }

        return WavinSelection.SelectRooms(rooms, filter);
    }
}
