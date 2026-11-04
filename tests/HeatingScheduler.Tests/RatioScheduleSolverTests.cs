using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

public class RatioScheduleSolverTests
{
    /// <summary>
    /// Independent check of the rules, written directly from the definition rather than the solver's state machine:
    /// off periods last at most <paramref name="maxOff"/> slots, and each on-block lasts at least
    /// max(<paramref name="minOn"/>, ⌈ratio × the preceding off period⌉). The final on-block may be cut short by the
    /// end of the horizon.
    /// </summary>
    internal static bool IsValid(IReadOnlyList<bool> on, HeatingHistory history, double ratio, int minOn, int maxOff)
    {
        int Required(int offSlots) => Math.Max(minOn, (int)Math.Ceiling(ratio * offSlots - 1e-9));

        var isOn = history.IsOn;
        var onLen = history.OnSlots;
        var offLen = history.OffSlots;      // when on: the off period before this on-block
        foreach (var slotOn in on)
        {
            if (slotOn)
            {
                if (!isOn)
                {
                    onLen = 0;              // offLen now holds the off period this block must make up for
                }

                onLen++;
                isOn = true;
            }
            else
            {
                if (isOn)
                {
                    if (onLen < Required(offLen))
                    {
                        return false;
                    }

                    offLen = 0;
                }

                offLen++;
                isOn = false;
                if (offLen > maxOff)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static decimal Cost(IReadOnlyList<bool> on, IReadOnlyList<decimal> prices) =>
        on.Select((o, i) => o ? prices[i] : 0m).Sum();

    /// <summary>
    /// Make-up on-slots still owed at the end of <paramref name="on"/>, from the definition: the rest of an unfinished
    /// on-block, or the whole make-up after a final off period.
    /// </summary>
    internal static int Owed(IReadOnlyList<bool> on, HeatingHistory history, double ratio, int minOn)
    {
        int Required(int offSlots) => Math.Max(minOn, (int)Math.Ceiling(ratio * offSlots - 1e-9));

        var isOn = history.IsOn;
        var onLen = history.OnSlots;
        var offLen = history.OffSlots;
        foreach (var slotOn in on)
        {
            if (slotOn)
            {
                if (!isOn)
                {
                    onLen = 0;
                }

                onLen++;
                isOn = true;
            }
            else
            {
                if (isOn)
                {
                    offLen = 0;
                }

                offLen++;
                isOn = false;
            }
        }

        return isOn ? Math.Max(0, Required(offLen) - onLen) : Required(offLen);
    }

    /// <summary>The solver's objective: in-plan cost plus owed make-up valued at the average price.</summary>
    private static decimal Objective(IReadOnlyList<bool> on, IReadOnlyList<decimal> prices, HeatingHistory history, double ratio, int minOn) =>
        Cost(on, prices) + Owed(on, history, ratio, minOn) * prices.Average();

    public static TheoryData<double, int, int, bool, int, int> BruteForceCases()
    {
        var data = new TheoryData<double, int, int, bool, int, int>();
        foreach (var ratio in new[] { 0.25, 0.5, 1.0, 1.5, 2.0 })
        foreach (var minOn in new[] { 1, 2 })
        foreach (var maxOff in new[] { 1, 3, 6 })
        foreach (var (isOn, onSlots, offSlots) in new[] { (true, 2, 0), (true, 1, 4), (false, 0, 1), (false, 0, 3) })
        {
            data.Add(ratio, minOn, maxOff, isOn, onSlots, offSlots);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void Matches_brute_force_optimum(double ratio, int minOn, int maxOff, bool isOn, int onSlots, int offSlots)
    {
        var history = new HeatingHistory(isOn, onSlots, offSlots);
        var rules = new RatioRules(ratio, minOn, maxOff);
        var random = new Random(HashCode.Combine(ratio, minOn, maxOff, isOn, onSlots, offSlots) & 0x7fffffff);
        const int n = 12;

        for (var trial = 0; trial < 5; trial++)
        {
            var prices = Enumerable.Range(0, n).Select(_ => (decimal)random.Next(-5, 60)).ToArray();

            var best = decimal.MaxValue;
            for (var mask = 0; mask < 1 << n; mask++)
            {
                var candidate = Enumerable.Range(0, n).Select(i => (mask >> i & 1) == 1).ToArray();
                if (IsValid(candidate, history, ratio, minOn, maxOff))
                {
                    best = Math.Min(best, Objective(candidate, prices, history, ratio, minOn));
                }
            }

            var result = RatioScheduleSolver.Solve(prices, history, rules);

            Assert.True(IsValid(result.On, history, ratio, minOn, maxOff), $"Invalid plan for prices [{string.Join(",", prices)}]");
            Assert.Equal(Cost(result.On, prices), result.Cost);
            Assert.Equal(Owed(result.On, history, ratio, minOn), result.OwedOnSlots);
            Assert.Equal(best, Objective(result.On, prices, history, ratio, minOn));
        }
    }

    [Fact]
    public void Off_through_the_peak_then_makes_up_the_heat()
    {
        // Expensive peak for 4 slots, then cheap. Ratio 0.5: 4 slots off requires at least 2 on afterwards.
        // Ending with 1 slot off then 1 on (50p) beats ending with 2 off and owing 1 slot at the average price (62.75p).
        decimal[] prices = [100, 100, 100, 100, 1, 1, 50, 50];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(2), new RatioRules(0.5, 1, 4));

        Assert.Equal([false, false, false, false, true, true, false, true], result.On);
        Assert.Equal(0, result.OwedOnSlots);
    }

    [Fact]
    public void Off_period_never_exceeds_maximum()
    {
        var prices = Enumerable.Repeat(100m, 12).ToArray();
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(2), new RatioRules(0.5, 1, 3));

        Assert.True(IsValid(result.On, HeatingHistory.On(2), 0.5, 1, 3));
        Assert.True(LongestRun(result.On, false) <= 3);

        // 2 off + 1 on (⌈0.5 × 2⌉) is the most efficient block. Ending on a long off period no longer looks free:
        // the owed make-up is charged at the average price, so the best total is 4 on-slots' worth (400p).
        Assert.Equal(400m, result.Cost + result.OwedOnSlots * 100m);
    }

    [Fact]
    public void Makes_up_full_off_time_forced_beyond_maximum()
    {
        // Forced off for 6 slots (e.g. fallback) with a maximum of 3: must switch on and make up ⌈0.5 × 6⌉ = 3 slots.
        decimal[] prices = [100, 100, 100, 100, 100];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.Off(6), new RatioRules(0.5, 1, 3));

        Assert.Equal([true, true, true, false, false], result.On);
    }

    private static int LongestRun(IReadOnlyList<bool> on, bool state)
    {
        int best = 0, current = 0;
        foreach (var s in on)
        {
            current = s == state ? current + 1 : 0;
            best = Math.Max(best, current);
        }

        return best;
    }

    [Fact]
    public void Make_up_time_is_rounded_up()
    {
        var rules = new RatioRules(0.25, 1, 12);
        Assert.Equal(1, rules.RequiredOnSlotsAfter(1)); // 0.25 → 1
        Assert.Equal(1, rules.RequiredOnSlotsAfter(4)); // exactly 1
        Assert.Equal(2, rules.RequiredOnSlotsAfter(5)); // 1.25 → 2
        Assert.Equal(3, new RatioRules(0.25, 3, 12).RequiredOnSlotsAfter(4)); // MinOn wins
    }

    [Fact]
    public void Heats_through_negative_prices_even_mid_off_period()
    {
        // A single negative slot during what would otherwise be an off period is still taken.
        decimal[] prices = [50, 50, -40, 50, 50, 50];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.Off(1), new RatioRules(0.25, 1, 6));

        Assert.True(result.On[2]);
    }

    [Fact]
    public void Must_finish_make_up_time_from_history()
    {
        // On for 1 slot after 4 slots off with ratio 0.75 → 3 required, so 2 more on before it can switch off.
        decimal[] prices = [100, 100, 100, 100];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(1, afterOffSlots: 4), new RatioRules(0.75, 1, 4));

        Assert.True(result.On[0]);
        Assert.True(result.On[1]);
        Assert.True(IsValid(result.On, HeatingHistory.On(1, afterOffSlots: 4), 0.75, 1, 4));
    }

    [Fact]
    public void Owed_make_up_at_end_of_plan_is_charged_at_average_price()
    {
        // Without the charge, staying off for the whole (short) plan would look free. With it, heating now in the
        // cheap slots beats owing 2 slots at the average price.
        decimal[] prices = [5, 5, 60, 60, 60, 60];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(2), new RatioRules(0.5, 2, 6));

        Assert.True(result.On[0]);
        Assert.True(result.On[1]);
    }

    [Fact]
    public void Must_switch_on_when_off_history_reaches_maximum()
    {
        decimal[] prices = [100, 1, 1, 1];
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.Off(3), new RatioRules(0.5, 1, 3));
        Assert.True(result.On[0]);
    }

    [Fact]
    public void Empty_horizon_returns_empty_plan()
    {
        var result = RatioScheduleSolver.Solve([], HeatingHistory.On(2), new RatioRules(0.5, 2, 6));
        Assert.Empty(result.On);
    }

    [Fact]
    public void Solves_a_full_day_quickly()
    {
        var random = new Random(1);
        var prices = Enumerable.Range(0, 96).Select(_ => (decimal)random.Next(5, 50)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = RatioScheduleSolver.Solve(prices, HeatingHistory.On(2), new RatioRules(0.1, 2, 48));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(IsValid(result.On, HeatingHistory.On(2), 0.1, 2, 48));
    }
}
