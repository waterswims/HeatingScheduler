using System.Text.Json;
using HeatingScheduler.Control;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeatingScheduler.Tests;

public class HeatingControlServiceTests
{
    private sealed class FakeWavin(params WavinDevice[] devices) : IWavinClient
    {
        public List<(string Device, IReadOnlyList<RoomSetpoint> Setpoints)> Sent { get; } = [];

        public Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WavinDevice>>(devices);

        public Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken) =>
            Task.FromResult(devices.Single(d => d.Name == deviceName));

        public Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken)
        {
            Sent.Add((deviceName, setpoints));
            return Task.CompletedTask;
        }
    }

    private static WavinRoom Room(int id, string title, double setpoint, string vacation = "VACATION_MODE_OFF") => new()
    {
        Id = JsonSerializer.SerializeToElement(id),
        Title = title,
        SetpointTemperature = setpoint,
        VacationMode = vacation,
    };

    private static WavinDevice Device(string name, params WavinRoom[] rooms) => new()
    {
        Name = name,
        LastConfig = new WavinLastConfig { Sentio = new WavinSentioConfig { Rooms = rooms.ToList() } },
    };

    private static WavinDevice UnmigratedDevice(string name) => new()
    {
        Name = name,
        Platform = JsonSerializer.SerializeToElement("PLATFORM_LEGACY"),
    };

    private sealed class FakeSchedule(DesiredState state) : IHeatingSchedule
    {
        public DesiredState GetDesiredState(DateTimeOffset utcNow) => state;
        public void MarkFirstAttemptComplete() { }
        public Task WaitForFirstAttemptAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static readonly DesiredState On = new(HeatingState.On, false);
    private static readonly DesiredState Off = new(HeatingState.Off, false);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:10:00Z");

    private static HeatingControlService Create(FakeWavin wavin, DesiredState desired, WavinOptions? wavinOptions = null, DateTimeOffset? now = null) =>
        new(
            wavin,
            new FakeSchedule(desired),
            TestHelpers.Opt(wavinOptions ?? new WavinOptions()),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 21, OffTemperature = 17, MaxCheckIntervalMinutes = 5 }),
            new FakeTimeProvider(now ?? Now),
            NullLogger<HeatingControlService>.Instance);

    [Fact]
    public async Task Sets_only_mismatched_rooms_in_one_request_per_device()
    {
        var wavin = new FakeWavin(
            Device("devices/1", Room(1, "Lounge", 17), Room(2, "Kitchen", 21), Room(3, "Hall", 21.04)),
            Device("devices/2", Room(1, "Bedroom", 19.5)));

        await Create(wavin, On).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, wavin.Sent.Count);
        var (device1, sp1) = wavin.Sent[0];
        Assert.Equal("devices/1", device1);
        Assert.Equal([1], sp1.Select(s => s.RoomId.GetInt32()));
        Assert.All(sp1, s => Assert.Equal(21, s.SetpointTemperature));
        Assert.Equal("devices/2", wavin.Sent[1].Device);
    }

    [Fact]
    public async Task Sets_off_temperature_during_off_slots()
    {
        var wavin = new FakeWavin(Device("devices/1", Room(1, "Lounge", 21), Room(2, "Kitchen", 17)));

        await Create(wavin, Off).RunOnceAsync(CancellationToken.None);

        var (_, setpoints) = Assert.Single(wavin.Sent);
        var sp = Assert.Single(setpoints);
        Assert.Equal(1, sp.RoomId.GetInt32());
        Assert.Equal(17, sp.SetpointTemperature);
    }

    [Fact]
    public async Task Does_nothing_when_all_rooms_match()
    {
        var wavin = new FakeWavin(Device("devices/1", Room(1, "Lounge", 21), Room(2, "Kitchen", 21)));
        await Create(wavin, On).RunOnceAsync(CancellationToken.None);
        Assert.Empty(wavin.Sent);
    }

    [Fact]
    public async Task Applies_room_filter_by_title_or_id()
    {
        var wavin = new FakeWavin(Device("devices/1", Room(1, "Lounge", 17), Room(2, "Kitchen", 17), Room(3, "Hall", 17)));
        var options = new WavinOptions { Rooms = ["lounge", "3", "Nonexistent"] };

        await Create(wavin, On, options).RunOnceAsync(CancellationToken.None);

        var (_, setpoints) = Assert.Single(wavin.Sent);
        Assert.Equal([1, 3], setpoints.Select(s => s.RoomId.GetInt32()).Order());
    }

    [Fact]
    public async Task Skips_rooms_in_vacation_mode_and_unmigrated_devices()
    {
        var wavin = new FakeWavin(
            Device("devices/1", Room(1, "Lounge", 17, "VACATION_MODE_ON"), Room(2, "Kitchen", 17)),
            UnmigratedDevice("devices/legacy"));

        await Create(wavin, On).RunOnceAsync(CancellationToken.None);

        var (device, setpoints) = Assert.Single(wavin.Sent);
        Assert.Equal("devices/1", device);
        Assert.Equal([2], setpoints.Select(s => s.RoomId.GetInt32()));
    }

    [Fact]
    public async Task Uses_configured_device_only()
    {
        var wavin = new FakeWavin(Device("devices/1", Room(1, "Lounge", 17)), Device("devices/2", Room(1, "Bedroom", 17)));

        await Create(wavin, On, new WavinOptions { DeviceName = "devices/2" }).RunOnceAsync(CancellationToken.None);

        Assert.Equal("devices/2", Assert.Single(wavin.Sent).Device);
    }

    [Fact]
    public async Task Uses_fallback_state_without_schedule()
    {
        var wavin = new FakeWavin(Device("devices/1", Room(1, "Lounge", 17)));
        await Create(wavin, new DesiredState(HeatingState.On, IsFallback: true)).RunOnceAsync(CancellationToken.None);
        Assert.Equal(21, Assert.Single(Assert.Single(wavin.Sent).Setpoints).SetpointTemperature);
    }

    [Theory]
    [InlineData("2026-11-02T10:00:00Z", 5 * 60)]       // next boundary 30m away → capped at 5m
    [InlineData("2026-11-02T10:27:00Z", 3 * 60 + 15)]  // just after 10:30
    [InlineData("2026-11-02T10:29:59Z", 16)]
    public void NextDelay_wakes_after_slot_boundary_or_max_interval(string now, int expectedSeconds)
    {
        var service = Create(new FakeWavin(), On, now: DateTimeOffset.Parse(now));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), service.NextDelay(DateTimeOffset.Parse(now)));
    }
}
