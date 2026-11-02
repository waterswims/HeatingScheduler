using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

public class AgileDayTests
{
    [Fact]
    public void Winter_day_runs_23_to_23_utc_with_48_slots()
    {
        var day = new AgileDay(new DateOnly(2026, 11, 2));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T23:00:00Z"), day.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T23:00:00Z"), day.EndUtc);
        Assert.Equal(48, day.SlotCount);
        Assert.Equal(48, day.SlotStarts().Count());
    }

    [Fact]
    public void Summer_day_starts_at_22_utc()
    {
        var day = new AgileDay(new DateOnly(2026, 7, 1));
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T22:00:00Z"), day.StartUtc);
        Assert.Equal(48, day.SlotCount);
    }

    [Fact]
    public void Spring_clock_change_day_has_46_slots()
    {
        // Clocks go forward on Sunday 29 March 2026.
        Assert.Equal(46, new AgileDay(new DateOnly(2026, 3, 28)).SlotCount);
    }

    [Fact]
    public void Autumn_clock_change_day_has_50_slots()
    {
        // Clocks go back on Sunday 25 October 2026.
        Assert.Equal(50, new AgileDay(new DateOnly(2026, 10, 24)).SlotCount);
    }

    [Theory]
    [InlineData("2026-11-02T22:59:59Z", "2026-11-01")] // GMT: 22:59 local, still previous Agile day
    [InlineData("2026-11-02T23:00:00Z", "2026-11-02")] // GMT: 23:00 local, new Agile day
    [InlineData("2026-07-01T21:59:59Z", "2026-06-30")] // BST: 22:59 local
    [InlineData("2026-07-01T22:00:00Z", "2026-07-01")] // BST: 23:00 local
    [InlineData("2026-07-02T12:00:00Z", "2026-07-01")]
    public void Containing_maps_instant_to_correct_day(string instant, string expectedStartDate)
    {
        var day = AgileDay.Containing(DateTimeOffset.Parse(instant));
        Assert.Equal(DateOnly.Parse(expectedStartDate), day.StartDate);
        Assert.True(day.Contains(DateTimeOffset.Parse(instant)));
    }

    [Theory]
    [InlineData("2026-11-02T10:00:00Z", "2026-11-02T10:00:00Z")]
    [InlineData("2026-11-02T10:29:59Z", "2026-11-02T10:00:00Z")]
    [InlineData("2026-11-02T10:30:00Z", "2026-11-02T10:30:00Z")]
    [InlineData("2026-07-02T10:45:00+01:00", "2026-07-02T09:30:00Z")]
    public void SlotStartFor_floors_to_half_hour(string instant, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), AgileDay.SlotStartFor(DateTimeOffset.Parse(instant)));
    }
}
