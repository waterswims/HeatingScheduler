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

    private static readonly TuningSettings Settings = new(MinTemperature: 21.0, OnTemperature: 23.0);

    /// <summary>Builds readings every 5 minutes from a list of (state, minutes, temperature at each reading) phases.</summary>
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

        /// <summary>Adds a phase of <paramref name="minutes"/> with temperature changing by <paramref name="perHour"/> °C/h, capped at <paramref name="cap"/>.</summary>
        public SeriesBuilder Phase(HeatingState state, int minutes, double perHour, double cap = double.MaxValue)
        {
            for (var m = 0; m < minutes; m += 5)
            {
                _readings.Add(new RoomReading(_t, Math.Round(_temp, 2), state));
                _t += TimeSpan.FromMinutes(5);
                _temp = Math.Min(cap, _temp + perHour / 12);
            }

            return this;
        }

        public SeriesBuilder Gap(int minutes)
        {
            _t += TimeSpan.FromMinutes(minutes);
            return this;
        }

        public RoomSeries Build() => new(name, name, _readings);
    }

    [Fact]
    public void Works_out_max_off_and_ratio_from_cooling_and_heating()
    {
        // Off 4h cooling 0.5°C/h from 23°C → (23 − 21) / 0.5 = 4h max off.
        // Then a finished 2h on period heating 2°C/h → ratio 0.5 / 2 = 0.25.
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 240, -0.5)
            .Phase(HeatingState.On, 120, 2.0, cap: 23.0)
            .Phase(HeatingState.Off, 30, -0.5)
            .Build();

        var result = ThermalEstimator.Estimate([room], Settings);
        var estimate = Assert.Single(result.Rooms);

        Assert.Equal(0.5, estimate.CoolingPerHour!.Value, 2);
        Assert.Equal(240, estimate.MaxOffMinutes!.Value, 0);
        Assert.Equal(240, result.MaxOffMinutes);
        Assert.Equal(1, estimate.HeatingPeriods);
        Assert.Equal(0.25, result.OnToOffRatio!.Value, 2);
    }

    [Fact]
    public void Heating_rate_stops_when_room_reaches_on_temperature()
    {
        // Heats at 1.5°C/h from 21°C, reaching 23°C after about 80 min, then holds for the rest of a 3h on period.
        // Using the whole on period would understate the heating rate.
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 240, -0.5)
            .Phase(HeatingState.On, 180, 1.5, cap: 23.0)
            .Phase(HeatingState.Off, 30, -0.5)
            .Build();

        var estimate = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1.5, estimate.HeatingPerHour!.Value, 1);
    }

    [Fact]
    public void Uses_whole_on_period_when_room_never_warms_up()
    {
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 240, -0.5)
            .Phase(HeatingState.On, 90, 1.0)
            .Phase(HeatingState.Off, 30, -0.5)
            .Build();

        var estimate = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1.0, estimate.HeatingPerHour!.Value, 1);
        Assert.Equal(0.5, estimate.OnToOffRatio!.Value, 2);
    }

    [Fact]
    public void Ignores_on_period_still_in_progress()
    {
        // The current on period's early readings mostly show the floor's lag, so only the finished one counts.
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 240, -0.5)
            .Phase(HeatingState.On, 120, 2.0, cap: 23.0)   // finished: 2°C/h
            .Phase(HeatingState.Off, 120, -0.5)
            .Phase(HeatingState.On, 90, 0.05)              // in progress: lag
            .Build();

        var estimate = Assert.Single(ThermalEstimator.Estimate([room], Settings).Rooms);

        Assert.Equal(1, estimate.HeatingPeriods);
        Assert.Equal(2.0, estimate.HeatingPerHour!.Value, 1);
    }

    [Fact]
    public void Ignores_on_periods_shorter_than_an_hour()
    {
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 240, -0.5)
            .Phase(HeatingState.On, 45, 2.0)
            .Phase(HeatingState.Off, 30, -0.5)
            .Build();

        var result = ThermalEstimator.Estimate([room], Settings);

        Assert.Equal(0, Assert.Single(result.Rooms).HeatingPeriods);
        Assert.Null(result.OnToOffRatio);
        Assert.Equal(240, result.MaxOffMinutes);
    }

    [Fact]
    public void Heating_rate_is_median_of_finished_on_periods()
    {
        // Three finished cycles heating at 1, 2 and 4°C/h: the median (2°C/h) resists one odd cycle.
        // Cooling from the longest off period (300 min at 0.4°C/h) → ratio 0.4 / 2 = 0.2.
        var room = new SeriesBuilder("Lounge")
            .Phase(HeatingState.Off, 300, -0.4)
            .Phase(HeatingState.On, 90, 1.0)
            .Phase(HeatingState.Off, 90, -0.4)
            .Phase(HeatingState.On, 90, 4.0, cap: 30)
            .Phase(HeatingState.Off, 90, -0.4)
            .StartAt(21.0)
            .Phase(HeatingState.On, 90, 2.0, cap: 30)
            .Phase(HeatingState.Off, 30, -0.4)
            .Build();

        var estimate = Assert.Single(ThermalEstimator.Estimate([room], Settings with { OnTemperature = 30 }).Rooms);

        Assert.Equal(3, estimate.HeatingPeriods);
        Assert.Equal(0.4, estimate.CoolingPerHour!.Value, 2);
        Assert.Equal(TimeSpan.FromMinutes(300), estimate.OffDuration);
        Assert.Equal(2.0, estimate.HeatingPerHour!.Value, 1);
        Assert.Equal(0.2, estimate.OnToOffRatio!.Value, 2);
    }

    [Fact]
    public void Worst_room_decides()
    {
        var mild = new SeriesBuilder("Mild")
            .Phase(HeatingState.Off, 240, -0.25).Phase(HeatingState.On, 90, 2.0).Phase(HeatingState.Off, 30, -0.25).Build();
        var draughty = new SeriesBuilder("Draughty")
            .Phase(HeatingState.Off, 240, -1.0).Phase(HeatingState.On, 90, 1.0).Phase(HeatingState.Off, 30, -1.0).Build();

        var result = ThermalEstimator.Estimate([mild, draughty], Settings);

        Assert.Equal(120, result.MaxOffMinutes);         // draughty: 2°C / 1°C/h
        Assert.Equal(1.0, result.OnToOffRatio!.Value, 2); // draughty: 1 / 1
    }

    [Fact]
    public void Max_off_is_rounded_down_to_whole_slots_and_clamped()
    {
        // 2°C / 0.7°C/h = 171 min → 150.
        var room = new SeriesBuilder("A").Phase(HeatingState.Off, 240, -0.7).Build();
        Assert.Equal(150, ThermalEstimator.Estimate([room], Settings).MaxOffMinutes);

        // Very fast cooling → below the lower bound → 60.
        var cold = new SeriesBuilder("B").Phase(HeatingState.Off, 120, -3.0).Build();
        Assert.Equal(60, ThermalEstimator.Estimate([cold], Settings).MaxOffMinutes);

        // Warming while off (sun) → no measurable cooling → upper bound.
        var sunny = new SeriesBuilder("C").Phase(HeatingState.Off, 120, 0.3).Build();
        var result = ThermalEstimator.Estimate([sunny], Settings);
        Assert.Equal(720, result.MaxOffMinutes);
        Assert.Null(result.OnToOffRatio);
    }

    [Fact]
    public void Off_period_starting_below_minimum_gives_lower_bound()
    {
        var room = new SeriesBuilder("A").StartAt(20.5).Phase(HeatingState.Off, 120, -0.5).Build();
        Assert.Equal(60, ThermalEstimator.Estimate([room], Settings).MaxOffMinutes);
    }

    [Fact]
    public void Returns_nothing_without_enough_data()
    {
        var shortOff = new SeriesBuilder("A").Phase(HeatingState.On, 60, 1.0).Phase(HeatingState.Off, 45, -0.5).Build();
        var result = ThermalEstimator.Estimate([shortOff], Settings);

        Assert.Null(result.MaxOffMinutes);
        Assert.Null(result.OnToOffRatio);
        Assert.NotNull(Assert.Single(result.Rooms).Note);
    }

    [Fact]
    public void Ratio_needs_a_measurable_rise()
    {
        // Underfloor lag: the room keeps cooling slightly throughout an on period.
        var room = new SeriesBuilder("A")
            .Phase(HeatingState.Off, 240, -0.5).Phase(HeatingState.On, 90, -0.1).Phase(HeatingState.Off, 30, -0.5).Build();
        var result = ThermalEstimator.Estimate([room], Settings);

        Assert.Equal(240, result.MaxOffMinutes);
        Assert.Null(result.OnToOffRatio);
        Assert.Contains("no measurable warming", Assert.Single(result.Rooms).Note);
    }

    [Fact]
    public void Gaps_split_periods()
    {
        // Two 50-minute off stretches separated by a 1h gap: neither reaches the 60 min minimum.
        var room = new SeriesBuilder("A")
            .Phase(HeatingState.Off, 50, -0.5)
            .Gap(60)
            .Phase(HeatingState.Off, 50, -0.5)
            .Build();

        Assert.Null(ThermalEstimator.Estimate([room], Settings).MaxOffMinutes);
    }

    [Fact]
    public void On_period_after_or_before_a_gap_is_not_used_for_heating()
    {
        var afterGap = new SeriesBuilder("A")
            .Phase(HeatingState.Off, 240, -0.5).Gap(60).Phase(HeatingState.On, 90, 2.0).Phase(HeatingState.Off, 30, -0.5).Build();
        var beforeGap = new SeriesBuilder("B")
            .Phase(HeatingState.Off, 240, -0.5).Phase(HeatingState.On, 90, 2.0).Gap(60).Phase(HeatingState.Off, 30, -0.5).Build();

        foreach (var room in new[] { afterGap, beforeGap })
        {
            var result = ThermalEstimator.Estimate([room], Settings);
            Assert.Equal(240, result.MaxOffMinutes);
            Assert.Null(result.OnToOffRatio);
        }
    }
}

public class ThermalTuningServiceTests
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

    /// <summary>4h off cooling 1°C/h from 23°C, 1.5h on heating 2°C/h, then 30 min off, as 5-minute rows ending at <see cref="Now"/>.</summary>
    private static List<RoomTimeSeriesRow> CoolThenHeat(double offSetpoint = 16, double onSetpoint = 23)
    {
        var rows = new List<RoomTimeSeriesRow>();
        var t = Now - TimeSpan.FromHours(6);
        var temp = 23.0;
        for (var m = 0; m < 240; m += 5, t += TimeSpan.FromMinutes(5), temp -= 1.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = offSetpoint });
        }

        for (var m = 0; m < 90; m += 5, t += TimeSpan.FromMinutes(5), temp += 2.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = onSetpoint });
        }

        for (var m = 0; m < 30; m += 5, t += TimeSpan.FromMinutes(5), temp -= 1.0 / 12)
        {
            rows.Add(new RoomTimeSeriesRow { Timestamp = t, AirTemperature = temp, SetpointTemperature = offSetpoint });
        }

        return rows;
    }

    private static (ThermalTuningService Service, HeatingSchedule Schedule) Create(FakeWavin wavin, WavinOptions? wavinOptions = null)
    {
        var schedule = new HeatingSchedule(
            TestHelpers.Opt(new ScheduleOptions { OnToOffRatio = 0.25, MaxOffMinutes = 600, MinOnMinutes = 60 }),
            NullLogger<HeatingSchedule>.Instance);
        var service = new ThermalTuningService(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions { MinTemperature = 21 }),
            TestHelpers.Opt(wavinOptions ?? new WavinOptions()),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 }),
            new FakeTimeProvider(Now),
            NullLogger<ThermalTuningService>.Instance);
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
        Assert.Equal(("1", Now - TimeSpan.FromHours(24), Now, 5), call);
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
        var service = new ThermalTuningService(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions { MinTemperature = 21, Rooms = ["lobby"] }),
            TestHelpers.Opt(new WavinOptions()),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 }),
            new FakeTimeProvider(Now),
            NullLogger<ThermalTuningService>.Instance);

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
