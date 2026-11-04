using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
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
        public List<(DateTimeOffset From, DateTimeOffset To)> Calls { get; } = [];

        public Task<IReadOnlyList<AgileSlot>> GetRatesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Calls.Add((fromUtc, toUtc));
            return Task.FromResult<IReadOnlyList<AgileSlot>>(
                available.Where(s => s.ValidFrom >= fromUtc && s.ValidFrom < toUtc).ToList());
        }
    }

    private static readonly DateTimeOffset Slot = DateTimeOffset.Parse("2026-11-04T12:00:00Z");

    private static (PriceFetchService Service, HeatingSchedule Schedule) Create(FakeOctopus octopus, DateTimeOffset now)
    {
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions()), NullLogger<HeatingSchedule>.Instance);
        var service = new PriceFetchService(
            octopus, schedule,
            new FakeTimeProvider(now),
            NullLogger<PriceFetchService>.Instance);
        return (service, schedule);
    }

    [Fact]
    public async Task Fetches_rolling_horizon_from_current_slot()
    {
        var octopus = new FakeOctopus(TestHelpers.SlotsFrom(Slot, 20, i => i));
        var (service, schedule) = Create(octopus, Slot.AddMinutes(10));

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal((Slot, Slot.AddHours(24)), Assert.Single(octopus.Calls));
        Assert.Equal(Slot + 20 * Slots.Length, schedule.PricesKnownUntil(Slot));
    }

    [Fact]
    public async Task Skips_fetch_when_whole_horizon_is_known()
    {
        var octopus = new FakeOctopus(TestHelpers.SlotsFrom(Slot, 60, i => i));
        var (service, _) = Create(octopus, Slot);

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(octopus.Calls);
    }

    [Fact]
    public async Task Fetches_again_while_horizon_is_incomplete()
    {
        var octopus = new FakeOctopus(TestHelpers.SlotsFrom(Slot, 20, i => i));
        var (service, _) = Create(octopus, Slot);

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, octopus.Calls.Count);
    }
}
