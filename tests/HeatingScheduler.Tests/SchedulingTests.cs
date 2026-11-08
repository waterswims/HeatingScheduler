using HeatingScheduler.Notifications;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using HeatingScheduler.Wavin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeatingScheduler.Tests;

public class SlotsTests
{
    [Theory]
    [InlineData("2026-11-02T10:00:00Z", "2026-11-02T10:00:00Z")]
    [InlineData("2026-11-02T10:29:59Z", "2026-11-02T10:00:00Z")]
    [InlineData("2026-11-02T10:30:00Z", "2026-11-02T10:30:00Z")]
    [InlineData("2026-07-02T10:45:00+01:00", "2026-07-02T09:30:00Z")]
    public void StartFor_floors_to_half_hour(string instant, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), Slots.StartFor(DateTimeOffset.Parse(instant)));
    }
}

public class HeatingScheduleTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

    private static HeatingSchedule Create(
        double min = 20.5, double cooling = 0.2, double heating = 0.4, int minOnMinutes = 60, HeatingState fallback = HeatingState.On) =>
        new(TestHelpers.Opt(new ScheduleOptions
            {
                MinTemperature = min,
                SafetyMargin = 0.2,
                CoolingRate = cooling,
                HeatingRate = heating,
                MinOnMinutes = minOnMinutes,
                FallbackState = fallback,
            }),
            TestHelpers.Opt(new HeatingOptions { OnTemperature = 22.5, OffTemperature = 16 }),
            NullLogger<HeatingSchedule>.Instance);

    private static RoomTemperature Lobby(double t, DateTimeOffset at) => new("d/3", "AT3 Lobby", t, at);

    [Theory]
    [InlineData(HeatingState.On)]
    [InlineData(HeatingState.Off)]
    public void Uses_fallback_when_no_prices(HeatingState fallback)
    {
        var schedule = Create(fallback: fallback);
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        Assert.Equal(new DesiredState(fallback, IsFallback: true), schedule.GetDesiredState(T0));
    }

    [Fact]
    public void Uses_fallback_without_a_recent_temperature()
    {
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0 - TimeSpan.FromHours(2))]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, _ => 10m), T0);

        Assert.True(schedule.GetDesiredState(T0).IsFallback);
    }

    [Fact]
    public void Plans_from_the_measured_temperature()
    {
        // Warm room and dear prices first: it stays off. Cold room: it must heat straight away.
        var warm = Create();
        warm.UpdateTemperatures([Lobby(22.0, T0)]);
        warm.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i < 4 ? 100m : 1m), T0);
        Assert.Equal(new DesiredState(HeatingState.Off, false), warm.GetDesiredState(T0));

        var cold = Create();
        cold.UpdateTemperatures([Lobby(20.6, T0)]);
        cold.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i < 4 ? 100m : 1m), T0);
        Assert.Equal(new DesiredState(HeatingState.On, false), cold.GetDesiredState(T0));
    }

    [Fact]
    public void Keeps_the_prediction_above_minimum_plus_margin()
    {
        var random = new Random(3);
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.4, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 48, _ => random.Next(10, 60)), T0);
        schedule.GetDesiredState(T0);

        Assert.All(schedule.Plan, p => Assert.True(p.LowestTemperature >= 20.7 - 1e-9));
    }

    [Fact]
    public void Committed_slot_does_not_change_when_new_prices_arrive()
    {
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 1m : 50m), T0);
        Assert.Equal(HeatingState.On, schedule.GetDesiredState(T0).State);

        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 500m : 1m), T0.AddMinutes(10));
        Assert.Equal(HeatingState.On, schedule.GetDesiredState(T0.AddMinutes(20)).State);
    }

    [Fact]
    public void Follows_the_plan_while_measurements_match_the_prediction()
    {
        var random = new Random(5);
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 40, _ => random.Next(10, 60)), T0);
        schedule.GetDesiredState(T0);
        var plan = schedule.Plan.ToList();

        // Feed back exactly the predicted temperatures: every decision is the planned one.
        for (var i = 0; i < 20; i++)
        {
            var slot = plan[i];
            schedule.UpdateTemperatures([Lobby(slot.StartTemperatures["d/3"], slot.Start)]);
            Assert.Equal(slot.State, schedule.GetDesiredState(slot.Start).State);
        }
    }

    [Fact]
    public void Re_plans_from_the_measured_temperature_every_hour()
    {
        // Cheap at the end only: the plan is to stay off as long as possible. The room turns out much colder than
        // predicted. Half an hour in, the plan is still followed; on the hour it is re-made and the heating comes on.
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(22.0, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 12, i => i < 8 ? 60m : 1m), T0);
        Assert.Equal(HeatingState.Off, schedule.GetDesiredState(T0).State);

        var halfHour = T0 + Slots.Length;
        schedule.UpdateTemperatures([Lobby(20.65, halfHour)]);
        Assert.Equal(HeatingState.Off, schedule.GetDesiredState(halfHour).State);

        var hour = T0 + 2 * Slots.Length;
        schedule.UpdateTemperatures([Lobby(20.6, hour)]);
        Assert.Equal(HeatingState.On, schedule.GetDesiredState(hour).State);
    }

    [Fact]
    public void Keeps_following_the_plan_when_temperatures_go_stale()
    {
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 12, i => i % 3 * 10m + 10m), T0);
        schedule.GetDesiredState(T0);
        var plan = schedule.Plan.ToList();

        // No readings for two hours (Wavin unreachable): decisions still come from the plan, not the fallback.
        for (var i = 0; i < 4; i++)
        {
            var d = schedule.GetDesiredState(plan[i].Start);
            Assert.False(d.IsFallback);
            Assert.Equal(plan[i].State, d.State);
        }
    }

    [Fact]
    public void Records_what_happened_for_each_slot()
    {
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => 10m + i), T0);
        schedule.GetDesiredState(T0);
        schedule.UpdateTemperatures([Lobby(21.4, T0 + Slots.Length)]);
        schedule.GetDesiredState(T0 + Slots.Length);

        var history = schedule.History(T0, T0 + 2 * Slots.Length);

        Assert.Equal([T0, T0 + Slots.Length], history.Select(h => h.Start));
        Assert.Equal([10m, 11m], history.Select(h => h.Price!.Value));
        Assert.Equal([21.5, 21.4], history.Select(h => h.MeasuredTemperatures["d/3"]));
        Assert.All(history, h => Assert.False(h.Decision.IsFallback));
    }

    [Fact]
    public void Plan_does_not_change_as_slots_pass_when_the_room_follows_the_prediction()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var schedule = Create(cooling: 0.15, heating: 0.3);
            schedule.UpdateTemperatures([Lobby(21.6, T0)]);
            schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 40, _ => random.Next(15, 70)), T0);
            schedule.GetDesiredState(T0);
            var initial = schedule.Plan.ToDictionary(p => p.Start, p => p.State);

            for (var i = 1; i < 39; i++)
            {
                var slot = T0 + i * Slots.Length;
                var predicted = schedule.Plan[0].StartTemperatures["d/3"];
                schedule.UpdateTemperatures([Lobby(predicted, slot)]);
                schedule.GetDesiredState(slot);
                Assert.All(schedule.Plan, p => Assert.Equal(initial[p.Start], p.State));
            }
        }
    }

    [Fact]
    public void Measured_rates_replace_the_defaults()
    {
        var schedule = Create(cooling: 0.15, heating: 0.15);
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdateRates([new RoomRates("d/3", "AT3 Lobby", 0.137, null)]);

        var room = Assert.Single(schedule.Rooms);
        Assert.Equal((0.137, 0.15, 21.5), (room.CoolingPerHour, room.HeatingPerHour, room.Temperature));
    }

    [Fact]
    public void Clock_stepping_back_before_first_decision_returns_latest_decision()
    {
        var schedule = Create();
        schedule.UpdateTemperatures([Lobby(21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 1m : 50m), T0);
        var decision = schedule.GetDesiredState(T0.AddMinutes(1));

        Assert.Equal(decision, schedule.GetDesiredState(T0.AddMinutes(-2)));
    }

    [Fact]
    public void PricesKnownUntil_stops_at_first_gap()
    {
        var schedule = Create();
        var slots = TestHelpers.SlotsFrom(T0, 6, i => i);
        slots.RemoveAt(4);
        schedule.UpdatePrices(slots, T0);

        Assert.Equal(T0 + 4 * Slots.Length, schedule.PricesKnownUntil(T0));
    }

    [Fact]
    public void DescribeBlocks_groups_consecutive_states_with_end_temperature()
    {
        var temps = new Dictionary<string, double>();
        var plan = new List<PlannedSlot>
        {
            new(T0, HeatingState.On, 1, temps, 21.6),
            new(T0 + Slots.Length, HeatingState.On, 1, temps, 21.8),
            new(T0 + 2 * Slots.Length, HeatingState.Off, 1, temps, 21.7),
        };
        Assert.Equal("ON Wed 12:00–13:00 (21.8°C), off Wed 13:00–13:30 (21.7°C)", HeatingSchedule.DescribeBlocks(plan));
    }
}

public class PriceFetchServiceTests
{
    private sealed class FakeOctopus(List<AgileSlot> available) : IOctopusAgileClient
    {
        public List<AgileSlot> Available { get; } = available;
        public List<(DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

        public Task<IReadOnlyList<AgileSlot>> GetRatesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Calls.Add((fromUtc, toUtc));
            return Task.FromResult<IReadOnlyList<AgileSlot>>(
                Available.Where(s => s.ValidFrom >= fromUtc && s.ValidFrom < toUtc).ToList());
        }
    }

    /// <summary>Wavin fake: one lobby at 21.6°C with no usable history, so tuning runs but changes no rates.</summary>
    private sealed class FakeWavin : IWavinClient
    {
        public int HistoryCalls { get; private set; }
        public int FailuresLeft { get; set; }

        private static WavinDevice Device => new()
        {
            Name = "devices/1",
            LastConfig = new WavinLastConfig
            {
                Sentio = new WavinSentioConfig
                {
                    Rooms = [new WavinRoom { Id = System.Text.Json.JsonSerializer.SerializeToElement("1"), Title = "Lobby", AirTemperature = 21.6 }],
                },
            },
        };

        public Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new HttpRequestException("Wavin unavailable");
            }

            return Task.FromResult<IReadOnlyList<WavinDevice>>([Device]);
        }

        public Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken) => Task.FromResult(Device);

        public Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RoomTimeSeriesRow>> GetRoomTimeSeriesAsync(
            string deviceName, string roomId, DateTimeOffset start, DateTimeOffset end, int bucketMinutes, CancellationToken cancellationToken)
        {
            HistoryCalls++;
            return Task.FromResult<IReadOnlyList<RoomTimeSeriesRow>>([]);
        }
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<(string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(string subject, string body, CancellationToken cancellationToken)
        {
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Slot = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

    private sealed record Rig(PriceFetchService Service, HeatingSchedule Schedule, FakeOctopus Octopus, FakeWavin Wavin, FakeEmail Email, FakeTimeProvider Time);

    private static Rig Create(int slots, DateTimeOffset? now = null)
    {
        var octopus = new FakeOctopus(TestHelpers.SlotsFrom(Slot, slots, i => 10 + i % 7));
        var heating = TestHelpers.Opt(new HeatingOptions { OnTemperature = 22.5, OffTemperature = 16 });
        var scheduleOptions = TestHelpers.Opt(new ScheduleOptions { MinTemperature = 20.5 });
        var schedule = new HeatingSchedule(scheduleOptions, heating, NullLogger<HeatingSchedule>.Instance);
        var wavin = new FakeWavin();
        var email = new FakeEmail();
        var time = new FakeTimeProvider(now ?? Slot.AddMinutes(10));
        var tuner = new ThermalTuner(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions()), scheduleOptions,
            TestHelpers.Opt(new WavinOptions()),
            heating, time, NullLogger<ThermalTuner>.Instance);
        var notifier = new PlanNotifier(
            email, schedule,
            TestHelpers.Opt(new EmailOptions { Enabled = true, To = ["me@example.com"], SmtpHost = "smtp.example.com", From = "app@example.com" }),
            heating, NullLogger<PlanNotifier>.Instance);
        var service = new PriceFetchService(octopus, schedule, tuner, notifier, time, NullLogger<PriceFetchService>.Instance);
        return new Rig(service, schedule, octopus, wavin, email, time);
    }

    [Fact]
    public void Reads_temperatures_before_planning_so_the_first_slot_is_not_a_fallback()
    {
        var rig = Create(20);

        rig.Service.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        var snapshot = rig.Schedule.GetSnapshot();
        Assert.False(snapshot.Current!.IsFallback);
        Assert.Equal(21.6, Assert.Single(snapshot.Rooms).Temperature);
    }

    [Fact]
    public async Task Fetches_all_published_prices_and_plans_to_their_end()
    {
        var rig = Create(60);

        await rig.Service.RunOnceAsync(CancellationToken.None);

        Assert.Equal((Slot, Slot + HeatingSchedule.MaxHorizon), Assert.Single(rig.Octopus.Calls));
        Assert.Equal(Slot + 60 * Slots.Length, rig.Schedule.PricesKnownUntil(Slot));
        Assert.Equal(59, rig.Schedule.Plan.Count);
    }

    [Fact]
    public async Task Tunes_only_when_new_prices_arrive()
    {
        var rig = Create(20);

        await rig.Service.RunOnceAsync(CancellationToken.None);   // startup: tune
        await rig.Service.RunOnceAsync(CancellationToken.None);   // nothing new
        Assert.Equal(1, rig.Wavin.HistoryCalls);

        rig.Octopus.Available.AddRange(TestHelpers.SlotsFrom(Slot + 20 * Slots.Length, 10, _ => 5m));
        await rig.Service.RunOnceAsync(CancellationToken.None);   // new prices: tune again
        Assert.Equal(2, rig.Wavin.HistoryCalls);
    }

    [Fact]
    public async Task Retries_tuning_at_next_check_after_failure()
    {
        var rig = Create(20);
        rig.Wavin.FailuresLeft = 1;

        await rig.Service.RunOnceAsync(CancellationToken.None);   // Wavin fails
        await rig.Service.RunOnceAsync(CancellationToken.None);   // no new prices, but tuning still due
        await rig.Service.RunOnceAsync(CancellationToken.None);   // done

        Assert.Equal(1, rig.Wavin.HistoryCalls);
    }

    [Fact]
    public async Task Emails_only_when_new_prices_arrive()
    {
        var rig = Create(20);

        await rig.Service.RunOnceAsync(CancellationToken.None);
        var (subject, body) = Assert.Single(rig.Email.Sent);
        Assert.StartsWith("Heating plan: on ", subject);
        Assert.Contains("New Agile prices until", body);
        Assert.Contains("this is the first email since the service started", body);

        // Hours later, with the plan re-made hourly but no new prices: no email.
        for (var i = 0; i < 6; i++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(30));
            rig.Schedule.UpdateTemperatures([new RoomTemperature("devices/1/1", "Lobby", 21.6 - 0.05 * i, rig.Time.GetUtcNow())]);
            rig.Schedule.GetDesiredState(rig.Time.GetUtcNow());
            await rig.Service.RunOnceAsync(CancellationToken.None);
        }

        Assert.Single(rig.Email.Sent);

        rig.Octopus.Available.AddRange(TestHelpers.SlotsFrom(Slot + 20 * Slots.Length, 10, _ => 5m));
        await rig.Service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, rig.Email.Sent.Count);
        Assert.Contains("WHAT HAPPENED (Wed 12:00 – 15:00)", rig.Email.Sent[1].Body);
    }
}
