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
        double ratio = 0.5, int maxOffMinutes = 180, int minOnMinutes = 60, HeatingState fallback = HeatingState.On) =>
        new(TestHelpers.Opt(new ScheduleOptions
            {
                OnToOffRatio = ratio,
                MaxOffMinutes = maxOffMinutes,
                MinOnMinutes = minOnMinutes,
                FallbackState = fallback,
            }),
            NullLogger<HeatingSchedule>.Instance);

    [Theory]
    [InlineData(HeatingState.On)]
    [InlineData(HeatingState.Off)]
    public void Uses_fallback_when_no_prices(HeatingState fallback)
    {
        var schedule = Create(fallback: fallback);
        Assert.Equal(new DesiredState(fallback, IsFallback: true), schedule.GetDesiredState(T0));
    }

    [Fact]
    public void Startup_assumption_allows_staying_off_at_first()
    {
        // Assumed on for the minimum with nothing to make up → may switch off straight away.
        var schedule = Create();
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i < 4 ? 100m : 1m), T0);

        Assert.Equal(new DesiredState(HeatingState.Off, false), schedule.GetDesiredState(T0));
    }

    [Fact]
    public void Committed_slot_does_not_change_when_new_prices_arrive()
    {
        var schedule = Create();
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 1m : 50m), T0);
        Assert.Equal(HeatingState.On, schedule.GetDesiredState(T0).State);

        // New prices make the current slot expensive, but its decision is already committed.
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 500m : 1m), T0.AddMinutes(10));
        Assert.Equal(HeatingState.On, schedule.GetDesiredState(T0.AddMinutes(20)).State);
    }

    [Fact]
    public void Follows_rules_across_slots_as_time_passes()
    {
        var random = new Random(7);
        var prices = TestHelpers.SlotsFrom(T0, 48, _ => random.Next(5, 60));
        var schedule = Create(ratio: 0.75, maxOffMinutes: 120, minOnMinutes: 60);
        schedule.UpdatePrices(prices, T0);

        var decisions = new List<bool>();
        for (var i = 0; i < 40; i++)
        {
            decisions.Add(schedule.GetDesiredState(T0 + i * Slots.Length + TimeSpan.FromMinutes(1)).State == HeatingState.On);
        }

        Assert.True(RatioScheduleSolverTests.IsValid(decisions, HeatingHistory.On(2), 0.75, 2, 4));
    }

    [Fact]
    public void Exposes_plan_after_current_slot()
    {
        var schedule = Create();
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 6, i => i), T0);
        schedule.GetDesiredState(T0);

        Assert.Equal(5, schedule.Plan.Count);
        Assert.Equal(T0 + Slots.Length, schedule.Plan[0].Start);
    }

    [Fact]
    public void UpdateRules_replans_with_new_maximum_off_period()
    {
        // Expensive for 10 slots, then cheap: with a 5h maximum the plan stays off throughout.
        var schedule = Create(ratio: 0.25, maxOffMinutes: 300);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 16, i => i < 10 ? 100m : 1m), T0);
        schedule.GetDesiredState(T0);
        Assert.DoesNotContain(schedule.Plan.Take(9), p => p.State == HeatingState.On);

        // Tighter maximum (1h): the plan must now heat during the expensive period.
        schedule.UpdateRules(0.25, maxOffSlots: 2);
        Assert.Equal(2, schedule.Rules.MaxOffSlots);
        Assert.Contains(schedule.Plan.Take(9), p => p.State == HeatingState.On);
    }

    [Fact]
    public void Clock_stepping_back_before_first_decision_returns_latest_decision()
    {
        var schedule = Create();
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => i == 0 ? 1m : 50m), T0);
        var decision = schedule.GetDesiredState(T0.AddMinutes(1));

        // E.g. an NTP correction steps the clock back across the slot boundary.
        Assert.Equal(decision, schedule.GetDesiredState(T0.AddMinutes(-2)));
    }

    [Theory]
    [InlineData(1.0, 600)]
    [InlineData(0.5, 300)]
    [InlineData(1.5, 360)]
    public void Plan_does_not_change_as_slots_pass_without_new_prices(double ratio, int maxOffMinutes)
    {
        // Re-planning from each new slot (with the actual history) must give the same plan, otherwise the plan
        // would change, and an email be sent, every half hour.
        for (var seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            var schedule = Create(ratio: ratio, maxOffMinutes: maxOffMinutes, minOnMinutes: 60);
            schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 40, _ => random.Next(15, 70)), T0);
            schedule.GetDesiredState(T0);
            var initial = schedule.Plan.ToDictionary(p => p.Start, p => p.State);

            for (var i = 1; i < 39; i++)
            {
                schedule.GetDesiredState(T0 + i * Slots.Length);
                Assert.All(schedule.Plan, p => Assert.Equal(initial[p.Start], p.State));
            }
        }
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
    public void DescribeBlocks_groups_consecutive_states()
    {
        var plan = new List<PlannedSlot>
        {
            new(T0, HeatingState.On, 1),
            new(T0 + Slots.Length, HeatingState.On, 1),
            new(T0 + 2 * Slots.Length, HeatingState.Off, 1),
        };
        Assert.Equal("ON Wed 12:00–13:00, off Wed 13:00–13:30", HeatingSchedule.DescribeBlocks(plan));
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

    /// <summary>Wavin fake for tuning: one room with no usable history, so tuning runs but changes nothing.</summary>
    private sealed class FakeWavin : IWavinClient
    {
        public int HistoryCalls { get; private set; }
        public int FailuresLeft { get; set; }

        private static WavinDevice Device => new()
        {
            Name = "devices/1",
            LastConfig = new WavinLastConfig
            {
                Sentio = new WavinSentioConfig { Rooms = [new WavinRoom { Id = System.Text.Json.JsonSerializer.SerializeToElement("1"), Title = "Lobby" }] },
            },
        };

        public Task<IReadOnlyList<WavinDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WavinDevice>>([Device]);

        public Task<WavinDevice> GetDeviceAsync(string deviceName, CancellationToken cancellationToken) => Task.FromResult(Device);

        public Task SetRoomSetpointsAsync(string deviceName, IReadOnlyList<RoomSetpoint> setpoints, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RoomTimeSeriesRow>> GetRoomTimeSeriesAsync(
            string deviceName, string roomId, DateTimeOffset start, DateTimeOffset end, int bucketMinutes, CancellationToken cancellationToken)
        {
            HistoryCalls++;
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new HttpRequestException("Wavin unavailable");
            }

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
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions()), NullLogger<HeatingSchedule>.Instance);
        var wavin = new FakeWavin();
        var email = new FakeEmail();
        var time = new FakeTimeProvider(now ?? Slot.AddMinutes(10));
        var heating = TestHelpers.Opt(new HeatingOptions { OnTemperature = 23, OffTemperature = 16 });
        var tuner = new ThermalTuner(
            wavin, schedule,
            TestHelpers.Opt(new TuningOptions { MinTemperature = 21 }),
            TestHelpers.Opt(new WavinOptions()),
            heating, time, NullLogger<ThermalTuner>.Instance);
        var notifier = new PlanNotifier(
            email,
            TestHelpers.Opt(new EmailOptions { Enabled = true, To = ["me@example.com"], SmtpHost = "smtp.example.com", From = "app@example.com" }),
            heating, NullLogger<PlanNotifier>.Instance);
        var service = new PriceFetchService(octopus, schedule, tuner, notifier, time, NullLogger<PriceFetchService>.Instance);
        return new Rig(service, schedule, octopus, wavin, email, time);
    }

    [Fact]
    public async Task Fetches_all_published_prices_and_plans_to_their_end()
    {
        // 30 hours of prices: the plan covers all of them, not just 24 hours.
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

        await rig.Service.RunOnceAsync(CancellationToken.None);   // startup: new prices → tune
        await rig.Service.RunOnceAsync(CancellationToken.None);   // nothing new
        Assert.Equal(1, rig.Wavin.HistoryCalls);

        rig.Octopus.Available.AddRange(TestHelpers.SlotsFrom(Slot + 20 * Slots.Length, 10, _ => 5m));
        await rig.Service.RunOnceAsync(CancellationToken.None);   // new prices → tune again
        Assert.Equal(2, rig.Wavin.HistoryCalls);
    }

    [Fact]
    public async Task Retries_tuning_at_next_check_after_failure()
    {
        var rig = Create(20);
        rig.Wavin.FailuresLeft = 1;

        await rig.Service.RunOnceAsync(CancellationToken.None);   // tuning fails
        await rig.Service.RunOnceAsync(CancellationToken.None);   // no new prices, but tuning still due
        await rig.Service.RunOnceAsync(CancellationToken.None);   // done

        Assert.Equal(2, rig.Wavin.HistoryCalls);
    }

    [Fact]
    public async Task Emails_when_the_plan_changes_and_not_otherwise()
    {
        var rig = Create(20);

        await rig.Service.RunOnceAsync(CancellationToken.None);
        var (subject, body) = Assert.Single(rig.Email.Sent);
        Assert.StartsWith("Heating plan: on ", subject);
        Assert.Contains("new Agile prices until", body);

        // Half an hour later with no new prices: the plan is the same, so no email.
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await rig.Service.RunOnceAsync(CancellationToken.None);
        Assert.Single(rig.Email.Sent);

        // New prices extend the plan: email again.
        rig.Octopus.Available.AddRange(TestHelpers.SlotsFrom(Slot + 20 * Slots.Length, 10, _ => 5m));
        await rig.Service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, rig.Email.Sent.Count);
    }
}
