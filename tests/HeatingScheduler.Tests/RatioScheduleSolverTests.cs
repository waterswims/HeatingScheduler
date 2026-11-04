using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

public class RatioScheduleSolverTests
{
    /// <summary>
    /// Independent check of the rules, written directly from the definition rather than the solver's state machine.
    /// The final on-block may be cut short by the end of the horizon.
    /// </summary>
    internal static bool IsValid(IReadOnlyList<bool> on, HeatingHistory history, double ratio, int minOnSlots)
    {
        int MaxOff(int onSlots) => onSlots < minOnSlots ? 0 : (int)Math.Floor(ratio * onSlots + 1e-9);

        var isOn = history.IsOn;
        var onLen = history.OnSlots;
        var offLen = history.OffSlots;
        foreach (var slotOn in on)
        {
            if (slotOn)
            {
                onLen = isOn ? onLen + 1 : 1;
                offLen = 0;
                isOn = true;
            }
            else
            {
                offLen = isOn ? 1 : offLen + 1;
                isOn = false;
                if (offLen > MaxOff(onLen))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static decimal Cost(IReadOnlyList<bool> on, IReadOnlyList<decimal> prices) =>
        on.Select((o, i) => o ? prices[i] : 0m).Sum();

    public static TheoryData<double, int, bool, int, int> BruteForceCases()
    {
        var data = new TheoryData<double, int, bool, int, int>();
        foreach (var ratio in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
        foreach (var minOn in new[] { 1, 2, 3 })
        foreach (var (isOn, onSlots, offSlots) in new[] { (true, 1, 0), (true, 2, 0), (false, 2, 1), (false, 0, 0), (false, 4, 0) })
        {
            data.Add(ratio, minOn, isOn, onSlots, offSlots);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void Matches_brute_force_optimum(double ratio, int minOn, bool isOn, int onSlots, int offSlots)
    {
        var history = new HeatingHistory(isOn, onSlots, offSlots);
        var rules = new RatioRules(ratio, minOn);
        var random = new Random(HashCode.Combine(ratio, minOn, isOn, onSlots, offSlots) & 0x7fffffff);
        const int n = 12;

        for (var trial = 0; trial < 5; trial++)
        {
            var prices = Enumerable.Range(0, n).Select(_ => (decimal)random.Next(-5, 60)).ToArray();

            var best = decimal.MaxValue;
            for (var mask = 0; mask < 1 << n; mask++)
            {
                var candidate = Enumerable.Range(0, n).Select(i => (mask >> i & 1) == 1).ToArray();
                if (IsValid(candidate, history, ratio, minOn))
                {
                    best = Math.Min(best, Cost(candidate, prices));
                }
            }

            var result = RatioScheduleSolver.Solve(prices, history, rules);

            Assert.True(IsValid(result.On, history, ratio, minOn), $"Invalid plan for prices [{string.Join(",", prices)}]");
            Assert.Equal(Cost(result.On, prices), result.Cost);
            Assert.Equal(best, result.Cost);
        }
    }

    [Fact]
    public void Heats_during_cheap_block_to_earn_off_time_during_peak()
    {
        // 4 cheap slots then 8 expensive ones; ratio 2 means 4 slots on allow 8 off.
        decimal[] prices = [1, 1, 1, 1, 100, 100, 100, 100, 100, 100, 100, 100];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.Off(0, 0), new RatioRules(2.0, 1));

        Assert.Equal([true, true, true, true, false, false, false, false, false, false, false, false], result.On);
        Assert.Equal(4m, result.Cost);
    }

    [Fact]
    public void Always_heats_when_prices_are_negative()
    {
        decimal[] prices = [-5, -1, 20, 20, -3, 20];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(10), new RatioRules(10.0, 1));

        Assert.True(result.On[0]);
        Assert.True(result.On[1]);
        Assert.True(result.On[4]);
        Assert.False(result.On[2]);
    }

    [Fact]
    public void Respects_minimum_on_block()
    {
        // Single cheap slots between expensive ones would be ideal, but on-blocks must be 3 slots long.
        decimal[] prices = [1, 50, 50, 1, 50, 50, 1, 50, 50, 1, 50, 50];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.Off(0, 0), new RatioRules(1.0, 3));

        Assert.True(IsValid(result.On, HeatingHistory.Off(0, 0), 1.0, 3));
        var runs = Runs(result.On).Where(r => r.On).ToList();
        Assert.All(runs.SkipLast(1), r => Assert.True(r.Length >= 3));
    }

    [Fact]
    public void Off_allowance_is_rounded_down_for_fractional_ratio()
    {
        var rules = new RatioRules(1.5, 1);
        Assert.Equal(1, rules.MaxOffSlotsAfter(1));
        Assert.Equal(3, rules.MaxOffSlotsAfter(2));
        Assert.Equal(4, rules.MaxOffSlotsAfter(3));
    }

    [Fact]
    public void Honours_history_off_allowance()
    {
        // Off for 1 slot after 2 on with ratio 1.5 → 2 more off slots allowed, then must heat.
        decimal[] prices = [100, 100, 100, 100];
        var history = HeatingHistory.Off(2, 1);
        var result = RatioScheduleSolver.Solve(prices, history, new RatioRules(1.5, 1));

        Assert.Equal([false, false, true, false], result.On);
    }

    [Fact]
    public void Must_continue_on_block_shorter_than_minimum()
    {
        decimal[] prices = [100, 1, 1, 1];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(1), new RatioRules(5.0, 2));
        Assert.True(result.On[0]);
    }

    [Fact]
    public void Empty_horizon_returns_empty_plan()
    {
        var result = RatioScheduleSolver.Solve([], HeatingHistory.On(2), new RatioRules(1.0, 2));
        Assert.Empty(result.On);
    }

    [Fact]
    public void Solves_a_full_day_quickly()
    {
        var random = new Random(1);
        var prices = Enumerable.Range(0, 48).Select(_ => (decimal)random.Next(5, 50)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(2), new RatioRules(0.1, 2));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(IsValid(result.On, HeatingHistory.On(2), 0.1, 2));
    }

    private static IEnumerable<(bool On, int Length)> Runs(IReadOnlyList<bool> on)
    {
        var i = 0;
        while (i < on.Count)
        {
            var j = i;
            while (j < on.Count && on[j] == on[i])
            {
                j++;
            }

            yield return (on[i], j - i);
            i = j;
        }
    }
}
