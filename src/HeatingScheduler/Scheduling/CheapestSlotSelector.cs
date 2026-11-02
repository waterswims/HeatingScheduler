using HeatingScheduler.Octopus;

namespace HeatingScheduler.Scheduling;

/// <summary>The "on" slots chosen for one Agile day.</summary>
public sealed record DaySchedule(
    AgileDay Day,
    IReadOnlySet<DateTimeOffset> OnSlotStarts,
    int SlotCount,
    decimal AverageOnPrice,
    decimal AverageDayPrice)
{
    public bool IsOn(DateTimeOffset slotStart) => OnSlotStarts.Contains(slotStart);
}

public static class CheapestSlotSelector
{
    /// <summary>
    /// Picks the cheapest <paramref name="onPercentage"/>% of <paramref name="slots"/> (rounded to the nearest slot).
    /// Ties are broken by the earliest slot.
    /// </summary>
    public static DaySchedule Select(AgileDay day, IReadOnlyCollection<AgileSlot> slots, double onPercentage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onPercentage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(onPercentage, 100);

        var count = (int)Math.Round(slots.Count * onPercentage / 100.0, MidpointRounding.AwayFromZero);

        var chosen = slots
            .OrderBy(s => s.PriceIncVat)
            .ThenBy(s => s.ValidFrom)
            .Take(count)
            .ToList();

        return new DaySchedule(
            day,
            chosen.Select(s => s.ValidFrom).ToHashSet(),
            slots.Count,
            chosen.Count > 0 ? chosen.Average(s => s.PriceIncVat) : 0m,
            slots.Count > 0 ? slots.Average(s => s.PriceIncVat) : 0m);
    }
}
