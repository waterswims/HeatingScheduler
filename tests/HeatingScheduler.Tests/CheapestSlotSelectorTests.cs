using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

public class CheapestSlotSelectorTests
{
    private static readonly AgileDay Day = new(new DateOnly(2026, 11, 2));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 12)]
    [InlineData(40, 19)]   // 19.2 → 19
    [InlineData(51, 24)]   // 24.48 → 24
    [InlineData(52.1, 25)] // 25.008 → 25
    [InlineData(100, 48)]
    public void Selects_rounded_percentage_of_slots(double percentage, int expected)
    {
        var slots = TestHelpers.SlotsFor(Day, i => i);
        var result = CheapestSlotSelector.Select(Day, slots, percentage);
        Assert.Equal(expected, result.OnSlotStarts.Count);
        Assert.Equal(48, result.SlotCount);
    }

    [Fact]
    public void Selects_the_cheapest_slots_including_negative_prices()
    {
        // Prices descend through the day, so the last slots are the cheapest.
        var slots = TestHelpers.SlotsFor(Day, i => 20 - i);
        var result = CheapestSlotSelector.Select(Day, slots, 25);

        var expected = slots.OrderBy(s => s.PriceIncVat).Take(12).Select(s => s.ValidFrom).ToHashSet();
        Assert.True(expected.SetEquals(result.OnSlotStarts));
        Assert.All(slots.Skip(36), s => Assert.True(result.IsOn(s.ValidFrom)));
        Assert.Contains(slots.Last().ValidFrom, result.OnSlotStarts); // price -27p
    }

    [Fact]
    public void Ties_are_broken_by_earliest_slot()
    {
        var slots = TestHelpers.SlotsFor(Day, _ => 10m);
        var result = CheapestSlotSelector.Select(Day, slots, 25);
        Assert.True(slots.Take(12).Select(s => s.ValidFrom).ToHashSet().SetEquals(result.OnSlotStarts));
    }

    [Fact]
    public void Computes_average_prices()
    {
        var slots = TestHelpers.SlotsFor(Day, i => i < 24 ? 10m : 30m);
        var result = CheapestSlotSelector.Select(Day, slots, 50);
        Assert.Equal(10m, result.AverageOnPrice);
        Assert.Equal(20m, result.AverageDayPrice);
    }
}
