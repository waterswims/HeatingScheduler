using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HeatingScheduler.Tests;

public class HeatingScheduleTests
{
    private static readonly AgileDay Day = new(new DateOnly(2026, 11, 2));

    [Theory]
    [InlineData(HeatingState.On)]
    [InlineData(HeatingState.Off)]
    public void Returns_fallback_when_no_schedule(HeatingState fallback)
    {
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions { FallbackState = fallback }));
        Assert.Equal(new DesiredState(fallback, IsFallback: true), schedule.GetDesiredState(Day.StartUtc));
    }

    [Fact]
    public void Returns_on_only_during_selected_slots()
    {
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions()));
        var slots = TestHelpers.SlotsFor(Day, i => i == 3 ? 1m : 50m);
        schedule.SetDay(CheapestSlotSelector.Select(Day, slots, 100.0 / 48));

        var onSlot = slots[3].ValidFrom;
        Assert.Equal(new DesiredState(HeatingState.On, false), schedule.GetDesiredState(onSlot));
        Assert.Equal(new DesiredState(HeatingState.On, false), schedule.GetDesiredState(onSlot.AddMinutes(29)));
        Assert.Equal(new DesiredState(HeatingState.Off, false), schedule.GetDesiredState(onSlot.AddMinutes(30)));
        Assert.Equal(new DesiredState(HeatingState.Off, false), schedule.GetDesiredState(onSlot.AddMinutes(-1)));
    }

    [Fact]
    public void RemoveDaysBefore_drops_finished_days()
    {
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions()));
        schedule.SetDay(CheapestSlotSelector.Select(Day, TestHelpers.SlotsFor(Day, i => i), 50));
        schedule.RemoveDaysBefore(Day.EndUtc.AddTicks(-1));
        Assert.True(schedule.HasDay(Day));
        schedule.RemoveDaysBefore(Day.EndUtc);
        Assert.False(schedule.HasDay(Day));
    }
}

public class PriceSchedulerServiceTests
{
    private sealed class FakeOctopus(List<AgileSlot> available) : IOctopusAgileClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgileSlot>> GetRatesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AgileSlot>>(
                available.Where(s => s.ValidFrom >= fromUtc && s.ValidFrom < toUtc).ToList());
        }
    }

    private static readonly AgileDay Today = new(new DateOnly(2026, 11, 2));

    private static (PriceSchedulerService Service, HeatingSchedule Schedule) Create(FakeOctopus octopus, DateTimeOffset now)
    {
        var schedule = new HeatingSchedule(TestHelpers.Opt(new ScheduleOptions()));
        var service = new PriceSchedulerService(
            octopus, schedule,
            TestHelpers.Opt(new OctopusOptions { ProductCode = "AGILE-24-10-01", RegionCode = "C" }),
            TestHelpers.Opt(new ScheduleOptions { OnPercentage = 25 }),
            new FakeTimeProvider(now),
            NullLogger<PriceSchedulerService>.Instance);
        return (service, schedule);
    }

    [Fact]
    public async Task Schedules_today_and_waits_for_tomorrow_until_complete()
    {
        var slots = TestHelpers.SlotsFor(Today, i => i);
        slots.AddRange(TestHelpers.SlotsFor(Today.Next, i => i).Take(10)); // tomorrow only partially published
        var octopus = new FakeOctopus(slots);
        var (service, schedule) = Create(octopus, Today.StartUtc.AddHours(12));

        await service.RunOnceAsync(CancellationToken.None);

        Assert.True(schedule.HasDay(Today));
        Assert.False(schedule.HasDay(Today.Next));
    }

    [Fact]
    public async Task Does_not_refetch_once_both_days_are_scheduled()
    {
        var slots = TestHelpers.SlotsFor(Today, i => i).Concat(TestHelpers.SlotsFor(Today.Next, i => i)).ToList();
        var octopus = new FakeOctopus(slots);
        var (service, schedule) = Create(octopus, Today.StartUtc.AddHours(18));

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.True(schedule.HasDay(Today));
        Assert.True(schedule.HasDay(Today.Next));
        Assert.Equal(1, octopus.Calls);
    }
}
