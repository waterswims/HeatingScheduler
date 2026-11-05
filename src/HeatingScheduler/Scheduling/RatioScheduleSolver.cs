namespace HeatingScheduler.Scheduling;

/// <summary>
/// The heating's recent history immediately before a slot.
/// <list type="bullet">
/// <item>On: on for <see cref="OnSlots"/> slots, following an off period of <see cref="OffSlots"/> slots.</item>
/// <item>Off: off for <see cref="OffSlots"/> slots (<see cref="OnSlots"/> is 0).</item>
/// </list>
/// </summary>
public readonly record struct HeatingHistory(bool IsOn, int OnSlots, int OffSlots)
{
    public static HeatingHistory On(int onSlots, int afterOffSlots = 0) => new(true, onSlots, afterOffSlots);

    public static HeatingHistory Off(int offSlots) => new(false, 0, offSlots);

    public HeatingHistory Advance(bool on) => on
        ? (IsOn ? this with { OnSlots = OnSlots + 1 } : On(1, OffSlots))
        : (IsOn ? Off(1) : this with { OffSlots = OffSlots + 1 });

    public override string ToString() => IsOn
        ? $"on for {OnSlots * 30} min after {OffSlots * 30} min off"
        : $"off for {OffSlots * 30} min";
}

/// <summary>
/// The scheduling rules:
/// <list type="bullet">
/// <item>an off period lasts at most <see cref="MaxOffSlots"/> slots;</item>
/// <item>the on-block after an off period of F slots must last at least ⌈<see cref="OnToOffRatio"/> × F⌉ slots,
/// to make up the heat lost, and never less than <see cref="MinOnSlots"/>.</item>
/// </list>
/// </summary>
public sealed record RatioRules(double OnToOffRatio, int MinOnSlots, int MaxOffSlots)
{
    /// <summary>Minimum length of the on-block that follows an off period of <paramref name="offSlots"/> slots.</summary>
    public int RequiredOnSlotsAfter(int offSlots) =>
        Math.Max(MinOnSlots, (int)Math.Ceiling(OnToOffRatio * offSlots - 1e-9));

    /// <summary>Whether the next slot may be off, given <paramref name="history"/>.</summary>
    public bool CanBeOff(HeatingHistory history) => history.IsOn
        ? history.OnSlots >= RequiredOnSlotsAfter(history.OffSlots) && MaxOffSlots >= 1
        : history.OffSlots < MaxOffSlots;
}

/// <summary>
/// <paramref name="Cost"/> is the sum of the prices of the "on" slots in the plan. <paramref name="OwedOnSlots"/> is the
/// make-up heating still owed when the plan ends.
/// </summary>
public sealed record SolveResult(IReadOnlyList<bool> On, decimal Cost, int OwedOnSlots);

/// <summary>
/// Finds the on/off sequence that obeys <see cref="RatioRules"/> with the lowest total price, using dynamic programming
/// over the half-hour slots. The result is exactly optimal. Ties are broken by fewest on/off switches.
/// <para>
/// The total is the sum of the prices of the "on" slots, plus any make-up heating still owed when the plan ends,
/// valued at <c>owedSlotPrice</c> per slot (by default the average price over the plan). Without that, every plan would
/// end with the longest allowed off period, as if the heat lost then were free to put back.
/// </para>
/// </summary>
public static class RatioScheduleSolver
{
    // DP state. On-phase: N = on slots still required before switching off. Off-phase: N = off slots so far.
    private readonly record struct State(bool IsOn, int N);

    private readonly record struct Node(decimal Cost, int Switches, State Previous, bool On);

    public static SolveResult Solve(IReadOnlyList<decimal> prices, HeatingHistory history, RatioRules rules, decimal? owedSlotPrice = null)
    {
        var n = prices.Count;
        if (n == 0)
        {
            return new SolveResult([], 0m, 0);
        }

        var start = history.IsOn
            ? new State(true, Math.Max(0, rules.RequiredOnSlotsAfter(history.OffSlots) - history.OnSlots))
            // Not capped: if the heating was forced off for longer than the maximum (fallback), the full off time must be made up.
            : new State(false, history.OffSlots);

        var layers = new List<Dictionary<State, Node>>(n + 1)
        {
            new() { [start] = new Node(0m, 0, start, start.IsOn) },
        };

        for (var i = 0; i < n; i++)
        {
            var next = new Dictionary<State, Node>();
            foreach (var (state, node) in layers[i])
            {
                foreach (var on in (ReadOnlySpan<bool>)[true, false])
                {
                    if (!TryStep(state, on, rules, out var nextState))
                    {
                        continue;
                    }

                    var candidate = new Node(
                        node.Cost + (on ? prices[i] : 0m),
                        node.Switches + (on != state.IsOn ? 1 : 0),
                        state,
                        on);

                    if (!next.TryGetValue(nextState, out var existing) || IsBetter(candidate, existing))
                    {
                        next[nextState] = candidate;
                    }
                }
            }

            layers.Add(next);
        }

        // Choose the end state including the value of make-up heating still owed (depends only on the end state,
        // so the comparison by in-plan cost above stays exact).
        var averagePrice = owedSlotPrice ?? prices.Average();
        var (bestState, best) = layers[n]
            .Select(kv => (State: kv.Key, Node: kv.Value with { Cost = kv.Value.Cost + Owed(kv.Key, rules) * averagePrice }))
            .Aggregate((a, b) => IsBetter(b.Node, a.Node) ? b : a);

        var result = new bool[n];
        var current = bestState;
        for (var i = n; i > 0; i--)
        {
            var node = layers[i][current];
            result[i - 1] = node.On;
            current = node.Previous;
        }

        return new SolveResult(result, layers[n][bestState].Cost, Owed(bestState, rules));
    }

    /// <summary>Make-up on-slots still owed in <paramref name="state"/>: the rest of the current on-block, or all of it after an off period.</summary>
    private static int Owed(State state, RatioRules rules) =>
        state.IsOn ? state.N : rules.RequiredOnSlotsAfter(state.N);

    private static bool IsBetter(Node a, Node b) =>
        a.Cost < b.Cost || (a.Cost == b.Cost && a.Switches < b.Switches);

    private static bool TryStep(State state, bool on, RatioRules rules, out State next)
    {
        if (on)
        {
            next = state.IsOn
                ? new State(true, Math.Max(state.N - 1, 0))
                : new State(true, rules.RequiredOnSlotsAfter(state.N) - 1);
            return true;
        }

        if (state.IsOn)
        {
            next = new State(false, 1);
            return state.N == 0 && rules.MaxOffSlots >= 1;
        }

        next = new State(false, state.N + 1);
        return state.N < rules.MaxOffSlots;
    }
}
