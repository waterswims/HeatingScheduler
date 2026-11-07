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

    private static readonly TuningSettings Settings = new(MinTemperature: 21.0);

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
    public void Works_out_max_off_and_ratio_between_turning_points()
    {
        // Fall 0.5°C/h from 23°C → (23 − 21) / 0.5 = 4h max off. Rise 1°C/h → ratio 0.5.
        // The final fall is still in progress, so it isn't measured.
        var room = new SeriesBuilder("Lounge").LeadIn().Phase(Off, 240, -0.5).Phase(On, 180, 1.0).Phase(Off, 120, -0.5).Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var e = Assert.Single(result.Rooms);

        Assert.Equal(1, e.CoolingPeriods);
        Assert.Equal(1, e.HeatingPeriods);
        Assert.InRange(e.CoolingPerHour!.Value, 0.45, 0.55);
        Assert.InRange(e.HeatingPerHour!.Value, 0.9, 1.1);
        Assert.InRange(result.OnToOffRatio!.Value, 0.45, 0.55);
        Assert.InRange(result.MaxOffMinutes!.Value, 210, 240);
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
        Assert.InRange(e.FallStartTemperature!.Value, 23.8, 24.1);
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
        Assert.Null(result.OnToOffRatio);
        Assert.NotNull(result.MaxOffMinutes);
    }

    [Fact]
    public void Heating_rate_is_the_median_of_the_rises()
    {
        // Rises at 1, 4 and 2°C/h: the median (2) resists one odd cycle. Falls at 0.4°C/h → ratio 0.2.
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
        Assert.InRange(result.OnToOffRatio!.Value, 0.17, 0.23);
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
        Assert.Null(result.OnToOffRatio);
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
    public void Worst_room_decides()
    {
        var mild = new SeriesBuilder("Mild").LeadIn().Phase(Off, 240, -0.25).Phase(On, 120, 2.0).Phase(Off, 60, -0.25).Build();
        var draughty = new SeriesBuilder("Draughty").LeadIn().Phase(Off, 240, -1.0).Phase(On, 120, 1.0).Phase(Off, 60, -1.0).Build();

        var result = ThermalEstimator.Estimate([mild, draughty], Settings);

        Assert.InRange(result.MaxOffMinutes!.Value, 90, 120);     // draughty: ~2°C / 1°C/h
        Assert.InRange(result.OnToOffRatio!.Value, 0.9, 1.1);     // draughty: 1 / 1
    }

    [Fact]
    public void Max_off_is_rounded_down_to_whole_slots_and_clamped()
    {
        // ~2°C / 0.7°C/h ≈ 170 min → 150.
        var room = new SeriesBuilder("A").LeadIn().Phase(Off, 240, -0.7).Phase(On, 60, 1.0).Build();
        Assert.Equal(150, ThermalEstimator.Estimate([room], Settings).MaxOffMinutes);

        // Very fast cooling → below the lower bound → 60.
        var cold = new SeriesBuilder("B").LeadIn().Phase(Off, 120, -3.0).Phase(On, 60, 2.0).Build();
        Assert.Equal(60, ThermalEstimator.Estimate([cold], Settings).MaxOffMinutes);

        // Slow cooling (2°C at 0.05°C/h = 40h) → above the upper bound → 1440 (24h).
        var slow = new SeriesBuilder("C").LeadIn().Phase(Off, 600, -0.05).Phase(On, 120, 1.0).Build();
        Assert.Equal(1440, ThermalEstimator.Estimate([slow], Settings).MaxOffMinutes);
    }

    [Fact]
    public void Fall_starting_below_minimum_gives_lower_bound()
    {
        var room = new SeriesBuilder("A").StartAt(20.5).LeadIn().Phase(Off, 120, -0.5).Phase(On, 60, 1.0).Build();
        Assert.Equal(60, ThermalEstimator.Estimate([room], Settings).MaxOffMinutes);
    }

    [Fact]
    public void Returns_nothing_without_enough_data()
    {
        var flat = new SeriesBuilder("A").Phase(Off, 240, -0.02).Phase(On, 120, 0.02).Build();
        var result = ThermalEstimator.Estimate([flat], Settings);

        Assert.Null(result.MaxOffMinutes);
        Assert.Null(result.OnToOffRatio);
        Assert.Contains("no finished fall", Assert.Single(result.Rooms).Note);
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
        Assert.Null(result.MaxOffMinutes);
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

    private static WavinRoom Room(string id, string title, string vacation = "VACATION_MODE_OFF") => new()
    {
        Id = System.Text.Json.JsonSerializer.SerializeToElement(id),
        Title = title,
        VacationMode = vacation,
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

    private static (ThermalTuner Service, HeatingSchedule Schedule) Create(FakeWavin wavin, WavinOptions? wavinOptions = null)
    {
        var schedule = new HeatingSchedule(
            TestHelpers.Opt(new ScheduleOptions { OnToOffRatio = 0.25, MaxOffMinutes = 600, MinOnMinutes = 60 }),
            NullLogger<HeatingSchedule>.Instance);
        var service = new ThermalTuner(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions { MinTemperature = 21 }),
            TestHelpers.Opt(wavinOptions ?? new WavinOptions()),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 }),
            new FakeTimeProvider(Now),
            NullLogger<ThermalTuner>.Instance);
        return (service, schedule);
    }

    [Fact]
    public async Task Applies_rules_worked_out_from_sentio_history()
    {
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat() }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(4, schedule.Rules.MaxOffSlots);         // (23 − 21) / 1°C/h = 2h
        Assert.Equal(0.5, schedule.Rules.OnToOffRatio, 2);   // 1 / 2
        Assert.Equal(2, schedule.Rules.MinOnSlots);          // unchanged

        var call = Assert.Single(wavin.HistoryCalls);
        Assert.Equal(("1", Now - TimeSpan.FromHours(60), Now, 5), call);
    }

    [Fact]
    public async Task Ignores_small_ratio_changes()
    {
        // Measured ratio 0.5; a current ratio within 10% of that is kept, avoiding re-plans on small wobbles.
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat() }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);
        schedule.UpdateRules(0.47, 20);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0.47, schedule.Rules.OnToOffRatio);
        Assert.Equal(4, schedule.Rules.MaxOffSlots);
    }

    [Fact]
    public async Task Keeps_configured_rules_without_usable_history()
    {
        var wavin = new FakeWavin([], Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0.25, schedule.Rules.OnToOffRatio);
        Assert.Equal(20, schedule.Rules.MaxOffSlots);
    }

    [Fact]
    public async Task Ignores_readings_with_other_setpoints()
    {
        // Setpoints that are neither the on nor the off temperature (e.g. set by hand) don't count as off or on.
        var wavin = new FakeWavin(new() { ["1"] = CoolThenHeat(offSetpoint: 19, onSetpoint: 21) }, Room("1", "Lounge"));
        var (service, schedule) = Create(wavin);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Lounge", Assert.Single(result.Rooms).Name);
        Assert.Null(result.MaxOffMinutes);
        Assert.Equal(20, schedule.Rules.MaxOffSlots);
    }

    [Fact]
    public async Task Tunes_only_against_configured_tuning_rooms()
    {
        var wavin = new FakeWavin(
            new() { ["1"] = CoolThenHeat(), ["2"] = CoolThenHeat(), ["3"] = CoolThenHeat() },
            Room("1", "Lounge"), Room("2", "Kitchen"), Room("3", "Lobby"));
        var schedule = new HeatingSchedule(
            TestHelpers.Opt(new ScheduleOptions { OnToOffRatio = 0.25, MaxOffMinutes = 600, MinOnMinutes = 60 }),
            NullLogger<HeatingSchedule>.Instance);
        var service = new ThermalTuner(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions { MinTemperature = 21, Rooms = ["lobby"] }),
            TestHelpers.Opt(new WavinOptions()),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 }),
            new FakeTimeProvider(Now),
            NullLogger<ThermalTuner>.Instance);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["3"], wavin.HistoryCalls.Select(c => c.RoomId));
        Assert.Equal("Lobby", Assert.Single(result.Rooms).Name);
    }

    [Fact]
    public async Task Uses_only_selected_rooms_not_on_vacation()
    {
        var wavin = new FakeWavin(
            new() { ["1"] = CoolThenHeat(), ["2"] = CoolThenHeat(), ["3"] = CoolThenHeat() },
            Room("1", "Lounge"), Room("2", "Kitchen", vacation: "VACATION_MODE_ON"), Room("3", "Hall"));
        var (service, _) = Create(wavin, new WavinOptions { Rooms = ["Lounge", "Kitchen"] });

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["1"], wavin.HistoryCalls.Select(c => c.RoomId));
    }
}
