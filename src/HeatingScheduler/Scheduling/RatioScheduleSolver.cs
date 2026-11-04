namespace HeatingScheduler.Scheduling;

/// <summary>
/// The heating's recent history immediately before a slot: either on for <see cref="OnSlots"/> slots,
/// or off for <see cref="OffSlots"/> slots after an on-block of <see cref="OnSlots"/> slots.
/// </summary>
public readonly record struct HeatingHistory(bool IsOn, int OnSlots, int OffSlots)
{
    public static HeatingHistory On(int onSlots) => new(true, onSlots, 0);

    public static HeatingHistory Off(int lastOnSlots, int offSlots) => new(false, lastOnSlots, offSlots);

    public HeatingHistory Advance(bool on) => on
        ? (IsOn ? this with { OnSlots = OnSlots + 1 } : On(1))
        : (IsOn ? Off(OnSlots, 1) : this with { OffSlots = OffSlots + 1 });

    public override string ToString() => IsOn
        ? $"on for {OnSlots * 30} min"
        : $"off for {OffSlots * 30} min after {OnSlots * 30} min on";
}

/// <summary>
/// The scheduling rules: an on-block must last at least <see cref="MinOnSlots"/> slots, and may be followed
/// by at most ⌊<see cref="OffToOnRatio"/> × its length⌋ off slots.
/// </summary>
public sealed record RatioRules(double OffToOnRatio, int MinOnSlots)
{
    /// <summary>Maximum number of off slots allowed after an on-block of <paramref name="onSlots"/> slots.</summary>
    public int MaxOffSlotsAfter(int onSlots) =>
        onSlots < MinOnSlots ? 0 : (int)Math.Floor(OffToOnRatio * onSlots + 1e-9);

    /// <summary>Whether the next slot may be off, given <paramref name="history"/>.</summary>
    public bool CanBeOff(HeatingHistory history) => history.IsOn
        ? MaxOffSlotsAfter(history.OnSlots) >= 1
        : history.OffSlots < MaxOffSlotsAfter(history.OnSlots);
}

public sealed record SolveResult(IReadOnlyList<bool> On, decimal Cost);

/// <summary>
/// Finds the on/off sequence with the lowest total price (sum of the prices of "on" slots) that obeys
/// <see cref="RatioRules"/>, using dynamic programming over the half-hour slots. The result is exactly optimal.
/// Ties are broken by fewest on/off switches.
/// </summary>
public static class RatioScheduleSolver
{
    // DP state. On-phase: N = length of the current on-block (capped). Off-phase: N = off slots still allowed (capped).
    private readonly record struct State(bool IsOn, int N);

    private readonly record struct Node(decimal Cost, int Switches, State Previous, bool On);

    public static SolveResult Solve(IReadOnlyList<decimal> prices, HeatingHistory history, RatioRules rules)
    {
        var n = prices.Count;
        if (n == 0)
        {
            return new SolveResult([], 0m);
        }

        // Once an on-block reaches this length, it is long enough to switch off and to stay off for the rest of the
        // horizon, so longer blocks behave identically.
        var cap = Math.Max(1, rules.MinOnSlots);
        while (rules.MaxOffSlotsAfter(cap) <= n)
        {
            cap++;
        }

        var start = history.IsOn
            ? new State(true, Math.Min(history.OnSlots, cap))
            : new State(false, Math.Clamp(rules.MaxOffSlotsAfter(history.OnSlots) - history.OffSlots, 0, n));

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
                    if (!TryStep(state, on, rules, cap, n, out var nextState))
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

        var (bestState, best) = layers[n].Aggregate((a, b) => IsBetter(b.Value, a.Value) ? b : a);

        var result = new bool[n];
        var current = bestState;
        for (var i = n; i > 0; i--)
        {
            var node = layers[i][current];
            result[i - 1] = node.On;
            current = node.Previous;
        }

        return new SolveResult(result, best.Cost);
    }

    private static bool IsBetter(Node a, Node b) =>
        a.Cost < b.Cost || (a.Cost == b.Cost && a.Switches < b.Switches);

    private static bool TryStep(State state, bool on, RatioRules rules, int cap, int horizon, out State next)
    {
        if (on)
        {
            next = state.IsOn ? new State(true, Math.Min(state.N + 1, cap)) : new State(true, 1);
            return true;
        }

        if (state.IsOn)
        {
            var allowance = rules.MaxOffSlotsAfter(state.N);
            next = new State(false, Math.Min(allowance - 1, horizon));
            return allowance >= 1;
        }

        next = new State(false, state.N - 1);
        return state.N >= 1;
    }
}
