using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeatingScheduler.Tests;

public class ThermalEstimatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

    private static readonly TuningSettings Settings = new();

    /// <summary>
    /// Builds readings every 5 minutes. Each phase sets the heating state and how fast the temperature moves, so
    /// floor lag (still falling after switch-on) and disturbances (an oven, the sun) can be simulated.
    /// </summary>
    private sealed class SeriesBuilder(string name)
    {
        private readonly List<RoomReading> _readings = [];
        private DateTimeOffset _t = T0;
        private double _temp = 23.0;

        public SeriesBuilder StartAt(double temperature)
        {
            _temp = temperature;
            return this;
        }

        /// <summary>Adds <paramref name="minutes"/> with the heating in <paramref name="state"/> and the temperature changing by <paramref name="perHour"/> °C/h.</summary>
        public SeriesBuilder Phase(HeatingState state, int minutes, double perHour)
        {
            for (var m = 0; m < minutes; m += 5)
            {
                _readings.Add(new RoomReading(_t, Math.Round(_temp, 3), state));
                _t += TimeSpan.FromMinutes(5);
                _temp += perHour / 12;
            }

            return this;
        }

        /// <summary>
        /// Adds a 90-minute rise (heating off) ending at the current temperature, so the first peak of the series is a
        /// real turning point. The lead-in starts at the edge of the history, so it is never measured itself.
        /// </summary>
        public SeriesBuilder LeadIn()
        {
            _temp -= 0.75;
            return Phase(HeatingState.Off, 90, 0.5);
        }

        public SeriesBuilder Gap(int minutes)
        {
            _t += TimeSpan.FromMinutes(minutes);
            return this;
        }

        public RoomSeries Build() => new(name, name, _readings);
    }

    private const HeatingState On = HeatingState.On, Off = HeatingState.Off;

    [Fact]
    public void Measures_cooling_and_heating_between_turning_points()
    {
        // Fall 0.5°C/h, rise 1°C/h. The final fall is still in progress, so it isn't measured.
        var room = new SeriesBuilder("Lounge").LeadIn().Phase(Off, 240, -0.5).Phase(On, 180, 1.0).Phase(Off, 120, -0.5).Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(1, e.CoolingPeriods);
        Assert.Equal(1, e.HeatingPeriods);
        Assert.InRange(e.CoolingPerHour!.Value, 0.45, 0.55);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
    }

    [Fact]
    public void Floor_lag_is_left_out()
    {
        // After switch-on the room keeps falling for 45 min, and after switch-off keeps rising for 30 min.
        // Measuring between turning points still gives the true rates (1°C/h up, 0.5°C/h down).
        var room = new SeriesBuilder("Lounge").LeadIn()
            .Phase(Off, 240, -0.5)
            .Phase(On, 45, -0.5).Phase(On, 150, 1.0)
            .Phase(Off, 30, 1.0).Phase(Off, 120, -0.5)
            .Phase(On, 120, 1.0)
            .Build();

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, e.HeatingPeriods);
        Assert.Equal(2, e.CoolingPeriods);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
        Assert.InRange(e.CoolingPerHour!.Value, 0.45, 0.55);
    }

    [Fact]
    public void Short_spike_is_merged_into_the_fall_around_it()
    {
        // A 40-minute oven spike (+1°C and back) in the middle of a long fall doesn't split it into two.
        var room = new SeriesBuilder("Lounge")
            .StartAt(24.0).LeadIn()
            .Phase(Off, 150, -0.4).Phase(Off, 20, 3.0).Phase(Off, 20, -3.0).Phase(Off, 170, -0.4)
            .Phase(On, 150, 1.0)
            .Phase(Off, 60, -0.4)
            .Build();

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, e.CoolingPeriods);
        Assert.InRange(e.CoolingPerHour!.Value, 0.3, 0.45);
    }

    [Fact]
    public void Rise_with_the_heating_off_is_not_counted_as_heating()
    {
        // The sun warms the room by 0.5°C/h with the heating off: only the heated rise (1°C/h) counts.
        var room = new SeriesBuilder("Lounge").LeadIn()
            .Phase(Off, 240, -0.5).Phase(Off, 120, 0.5).Phase(Off, 120, -0.5)
            .Phase(On, 180, 1.0)
            .Phase(Off, 60, -0.5)
            .Build();

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, e.HeatingPeriods);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
        Assert.Equal(2, e.CoolingPeriods);
    }

    [Fact]
    public void Rise_with_the_heating_on_for_only_part_of_it_is_left_out()
    {
        var room = new SeriesBuilder("Lounge").LeadIn()
            .Phase(Off, 240, -0.5)
            .Phase(On, 60, 1.0).Phase(Off, 60, 1.0)     // on for half of the rise
            .Phase(Off, 120, -0.5)
            .Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(0, e.HeatingPeriods);
        Assert.Null(e.HeatingPerHour);
        Assert.NotNull(e.CoolingPerHour);
    }

    [Fact]
    public void Heating_rate_is_the_median_of_the_rises()
    {
        // Rises at 1, 4 and 2°C/h: the median (2) resists one odd cycle.
        var room = new SeriesBuilder("Lounge").LeadIn()
            .Phase(Off, 300, -0.4).Phase(On, 120, 1.0)
            .Phase(Off, 180, -0.4).Phase(On, 60, 4.0)
            .Phase(Off, 300, -0.4).Phase(On, 90, 2.0)
            .Phase(Off, 60, -0.4)
            .Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(3, e.HeatingPeriods);
        Assert.Equal(3, e.CoolingPeriods);
        Assert.InRange(e.HeatingPerHour!.Value, 1.8, 2.2);
        Assert.InRange(e.CoolingPerHour!.Value, 0.36, 0.44);
    }

    [Fact]
    public void Movement_in_progress_is_not_measured()
    {
        // The rise hasn't turned yet, so only the fall before it counts.
        var room = new SeriesBuilder("Lounge").LeadIn().Phase(Off, 240, -0.5).Phase(On, 180, 1.0).Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(1, e.CoolingPeriods);
        Assert.Equal(0, e.HeatingPeriods);
        Assert.Null(e.HeatingPerHour);
        Assert.Contains("no finished rise", e.Note);
    }

    [Fact]
    public void Movement_cut_off_by_the_start_of_the_history_is_not_measured()
    {
        // The history starts part-way up a slow rise (0.3°C/h). Its start isn't a real trough, so only the full rise
        // (1°C/h) counts; counting both would give a median of 0.65°C/h.
        var room = new SeriesBuilder("Lobby")
            .StartAt(22.5)
            .Phase(On, 120, 0.3)
            .Phase(Off, 240, -0.5)
            .Phase(On, 180, 1.0)
            .Phase(Off, 60, -0.5)
            .Build();

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, e.HeatingPeriods);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
        Assert.Equal(1, e.CoolingPeriods);
    }

    [Fact]
    public void Context_before_the_window_confirms_its_first_turning_point_but_is_not_counted()
    {
        // A long rise peaks 30 min into the window, then a fall and a rise follow. With only the window, the peak
        // can't be confirmed (just 0.25°C of the rise is visible), so the fall after it would be lost. With context
        // it is confirmed: the fall counts, while the rise into it, which starts before the window, doesn't.
        var room = new SeriesBuilder("Lobby")
            .LeadIn()
            .Phase(Off, 240, -0.5)
            .Phase(On, 240, 0.5)       // rise, ends 30 min into the window
            .Phase(Off, 240, -0.5)
            .Phase(On, 180, 1.0)
            .Phase(Off, 60, -0.5)
            .Build();
        var windowStart = room.Readings[0].At + TimeSpan.FromMinutes(90 + 240 + 210);

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings with { CountFrom = windowStart }).Rooms);

        Assert.Equal(1, e.CoolingPeriods);     // the fall after the confirmed peak
        Assert.Equal(1, e.HeatingPeriods);     // the full 1°C/h rise only
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
    }

    [Fact]
    public void Real_trough_at_the_start_of_the_history_is_kept()
    {
        // A fall of 0.5°C into the first trough confirms it, so the rise after it counts.
        var room = new SeriesBuilder("Lobby")
            .Phase(Off, 60, -0.5)
            .Phase(On, 180, 1.0)
            .Phase(Off, 120, -0.5)
            .Build();

        var e = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, e.HeatingPeriods);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
    }

    [Fact]
    public void Rooms_are_measured_separately()
    {
        var mild = new SeriesBuilder("Mild").LeadIn().Phase(Off, 240, -0.25).Phase(On, 120, 2.0).Phase(Off, 60, -0.25).Build();
        var draughty = new SeriesBuilder("Draughty").LeadIn().Phase(Off, 240, -1.0).Phase(On, 120, 1.0).Phase(Off, 60, -1.0).Build();

        var rooms = ThermalEstimator.Estimate([mild, draughty], Settings).Rooms;

        Assert.Equal(["Mild", "Draughty"], rooms.Select(r => r.Key));
        Assert.InRange(rooms[0].CoolingPerHour!.Value, 0.22, 0.28);
        Assert.InRange(rooms[1].CoolingPerHour!.Value, 0.9, 1.1);
        Assert.InRange(rooms[1].HeatingPerHour!.Value, 0.9, 1.1);
    }

    [Fact]
    public void Warming_while_off_is_not_measurable_cooling()
    {
        var sunny = new SeriesBuilder("C").LeadIn().Phase(Off, 120, 0.3).Phase(On, 120, 1.0).Build();
        Assert.Null(Assert.Single(ThermalEstimator.Estimate([sunny], Settings).Rooms).CoolingPerHour);
    }

    [Fact]
    public void Returns_nothing_without_enough_data()
    {
        var flat = new SeriesBuilder("A").Phase(Off, 240, -0.02).Phase(On, 120, 0.02).Build();
        var e = Assert.Single(ThermalEstimator.Estimate([flat], Settings).Rooms);

        Assert.Null(e.CoolingPerHour);
        Assert.Null(e.HeatingPerHour);
        Assert.Contains("no finished fall", e.Note);
    }

    [Fact]
    public void Movements_spanning_a_gap_are_left_out()
    {
        var room = new SeriesBuilder("A")
            .Phase(Off, 120, -0.5).Gap(60).Phase(Off, 120, -0.5)
            .Phase(On, 180, 1.0).Gap(60).Phase(On, 1, 1.0).Phase(Off, 120, -0.5)
            .Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(0, e.CoolingPeriods);
        Assert.Equal(0, e.HeatingPeriods);
        Assert.Null(e.CoolingPerHour);
    }
}

public class ThermalTunerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T18:00:00Z");

    private sealed class FakeWavin(Dictionary<string, List<RoomTimeSeriesRow>> history, params WavinRoom[] rooms) : IWavinClient
    {
        public List<(string RoomId, DateTimeOffset Start, DateTimeOffset End, int Bucket)> HistoryCalls { get; } = [];

        private WavinDevice Device => new()
        {
            Name = "devices/1",
            LastConfig = new WavinLastConfig { Sentio = new WavinSentioConfig { Rooms = rooms.ToList() } },
        };

        public Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WavinDevice>>([Device]);

        public Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken) => Task.FromResult(Device);

        public Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RoomTimeSeriesRow>> GetRoomTimeSeriesAsync(
            string deviceName, string roomId, DateTimeOffset start, DateTimeOffset end, int bucketMinutes, CancellationToken cancellationToken)
        {
            HistoryCalls.Add((roomId, start, end, bucketMinutes));
            return Task.FromResult<IReadOnlyList<RoomTimeSeriesRow>>(history.GetValueOrDefault(roomId) ?? []);
        }
    }

    private static WavinRoom Room(string id, string title, string vacation = "VACATION_MODE_OFF", double? air = 21.5) => new()
    {
        Id = System.Text.Json.JsonSerializer.SerializeToElement(id),
        Title = title,
        VacationMode = vacation,
        AirTemperature = air,
    };

    /// <summary>4h off cooling 1°C/h from 23°C, 1.5h on heating 2°C/h, then 1h off, as 5-minute rows ending at <see cref="Now"/>.</summary>
    private static List<RoomTimeSeriesRow> CoolThenHeat(double offSetpoint = 16, double onSetpoint = 23)
    {
        var rows = new List<RoomTimeSeriesRow>();
        var t = Now - TimeSpan.FromHours(8);
        var temp = 22.25;
        for (var m = 0; m < 90; m += 5, t += TimeSpan.FromMinutes(5), temp += 0.5 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = offSetpoint });   // lead-in rise
        }

        for (var m = 0; m < 240; m += 5, t += TimeSpan.FromMinutes(5), temp -= 1.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = offSetpoint });
        }

        for (var m = 0; m < 90; m += 5, t += TimeSpan.FromMinutes(5), temp += 2.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = onSetpoint });
        }

        for (var m = 0; m < 60; m += 5, t += TimeSpan.FromMinutes(5), temp -= 1.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = offSetpoint });
        }

        return rows;
    }

    private static (ThermalTuner Service, HeatingSchedule Schedule) Create(
        FakeWavin wavin, WavinOptions? wavinOptions = null, ScheduleOptions? scheduleOptions = null)
    {
        var options = TestHelpers.Opt(scheduleOptions ?? new ScheduleOptions { MinTemperature = 20.5, CoolingRate = 0.15, HeatingRate = 0.15 });
        var heating = TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 });
        var schedule = new HeatingSchedule(options, heating, NullLogger<HeatingSchedule>.Instance);
        var service = new ThermalTuner(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions()), options,
            TestHelpers.Opt(wavinOptions ?? new WavinOptions()),
            heating, new FakeTimeProvider(Now),
            NullLogger<ThermalTuner>.Instance);
        return (service, schedule);
    }

    [Fact]
    public async Task Applies_rates_measured_from_sentio_history_and_reads_temperatures()
    {
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat() }, Room("1", "Lounge", air: 21.7));
        var (service, schedule) = Create(wavin);

        await service.RunOnceAsync(CancellationToken.None);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal("devices/1/1", room.Key);
        Assert.Equal(1.0, room.CoolingPerHour, 2);
        Assert.Equal(2.0, room.HeatingPerHour, 2);
        Assert.Equal(21.7, room.Temperature);
        Assert.Equal(("1", Now - TimeSpan.FromHours(84), Now, 5), Assert.Single(wavin.HistoryCalls));
    }

    [Fact]
    public async Task Applies_every_measured_rate_however_small_the_change()
    {
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat() }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);
        schedule.UpdateRates([new RoomRates("devices/1/1", "Lounge", 0.99, 1.98)]);

        await service.RunOnceAsync(CancellationToken.None);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal(1.0, room.CoolingPerHour, 2);
        Assert.NotEqual(0.99, room.CoolingPerHour);
        Assert.Equal(2.0, room.HeatingPerHour, 2);
    }

    [Fact]
    public async Task Measurement_replaces_the_configured_rates()
    {
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat() }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin, scheduleOptions: new ScheduleOptions { MinTemperature = 20.5, CoolingRate = 0.95, HeatingRate = 1.9 });

        await service.RunOnceAsync(CancellationToken.None);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal(1.0, room.CoolingPerHour, 2);
        Assert.Equal(2.0, room.HeatingPerHour, 2);
    }

    [Fact]
    public async Task Keeps_configured_rates_without_usable_history()
    {
        var wavin = new FakeWavin([], Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);

        await service.RunOnceAsync(CancellationToken.None);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal((0.15, 0.15), (room.CoolingPerHour, room.HeatingPerHour));
    }

    [Fact]
    public async Task Works_after_the_on_and_off_temperatures_change()
    {
        // History recorded with 19°C off and 21°C on, while the settings now say 16 and 23: the rises and falls of the
        // setpoint still say when the heating was on, so the rates are the same as with matching temperatures.
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat(offSetpoint: 19, onSetpoint: 21) }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);

        await service.RunOnceAsync(CancellationToken.None);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal(1.0, room.CoolingPerHour, 2);
        Assert.Equal(2.0, room.HeatingPerHour, 2);
    }

    [Fact]
    public async Task Tracks_only_the_schedule_rooms()
    {
        var wavin = new FakeWavin(
            new() { ["1"] = CoolThenHeat(), ["2"] = CoolThenHeat(), ["3"] = CoolThenHeat() },
            Room("1", "Lounge"), Room("2", "Kitchen"), Room("3", "Lobby"));
        var (service, schedule) = Create(wavin, scheduleOptions: new ScheduleOptions { MinTemperature = 20.5, Rooms = ["lobby"] });

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["3"], wavin.HistoryCalls.Select(c => c.RoomId));
        Assert.Equal("Lobby", Assert.Single(result.Rooms).Name);
        Assert.Equal("Lobby", Assert.Single(schedule.Rooms).Name);
    }

    [Fact]
    public async Task Skips_rooms_not_controlled_or_on_vacation()
    {
        var wavin = new FakeWavin(
            new() { ["1"] = CoolThenHeat(), ["2"] = CoolThenHeat(), ["3"] = CoolThenHeat() },
            Room("1", "Lounge"), Room("2", "Kitchen", vacation: "VACATION_MODE_ON"), Room("3", "Hall"));
        var (service, _) = Create(wavin, new WavinOptions { Rooms = ["Lounge", "Kitchen"] });

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["1"], wavin.HistoryCalls.Select(c => c.RoomId));
    }
}

public class SetpointClassificationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-07T00:00:00Z");
    private static readonly HeatingOptions Heating = new() { OnTemperature = 23, OffTemperature = 16 };

    private static List<RoomTimeSeriesRow> Rows(params double?[] setpoints) =>
        setpoints.Select((s, i) => new RoomTimeSeriesRow { Timestamp = T0.AddMinutes(5 * i), AirTemperature = 22, SetpointTemperature = s }).ToList();

    private static string States(List<RoomReading> readings) =>
        string.Concat(readings.Select(r => r.State == HeatingState.On ? '1' : '0'));

    [Fact]
    public void Rise_turns_on_and_fall_turns_off()
    {
        Assert.Equal("000111000", States(ThermalTuner.ClassifyBySetpointChanges(Rows(16, 16, 16, 23, 23, 23, 16, 16, 16), Heating)));
    }

    [Fact]
    public void Readings_before_the_first_change_take_the_opposite_state()
    {
        Assert.Equal("1100", States(ThermalTuner.ClassifyBySetpointChanges(Rows(21, 21, 18, 18), Heating)));
        Assert.Equal("0011", States(ThermalTuner.ClassifyBySetpointChanges(Rows(18, 18, 21, 21), Heating)));
    }

    [Fact]
    public void Works_with_any_temperatures_and_intermediate_steps()
    {
        // A bucket spanning a switch can show an in-between setpoint; both steps are rises, so both are on.
        Assert.Equal("001111000", States(ThermalTuner.ClassifyBySetpointChanges(Rows(17, 17, 19.5, 22, 22, 22, 18, 18, 18), Heating)));
    }

    [Fact]
    public void Without_any_change_matches_the_configured_temperatures()
    {
        Assert.Equal("111", States(ThermalTuner.ClassifyBySetpointChanges(Rows(23, 23, 23), Heating)));
        Assert.Equal("000", States(ThermalTuner.ClassifyBySetpointChanges(Rows(16, 16, 16), Heating)));
        Assert.Empty(ThermalTuner.ClassifyBySetpointChanges(Rows(19, 19, 19), Heating));
    }

    [Fact]
    public void Missing_values_are_skipped_but_changes_are_still_followed()
    {
        var rows = Rows(16, 16, null, 23, 23, 23);
        rows[4].AirTemperature = null;    // no air temperature: left out, but its setpoint still counts

        var readings = ThermalTuner.ClassifyBySetpointChanges(rows, Heating);

        Assert.Equal("0011", States(readings));
        Assert.Equal(4, readings.Count);
    }
}
