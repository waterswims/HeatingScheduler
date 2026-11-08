namespace HeatingScheduler.Scheduling;

/// <summary>A room the plan keeps warm enough: its temperature at the start of the plan and how fast it cools and heats.</summary>
public sealed record RoomState(string Key, double Temperature, double CoolingPerHour, double HeatingPerHour);

/// <param name="Floor">Lowest temperature any tracked room may be predicted to reach (minimum + safety margin).</param>
/// <param name="OnTemperature">Setpoint while on: heating can't take a room above it.</param>
/// <param name="OffTemperature">Setpoint while off: a room can't cool below it.</param>
/// <param name="MinOnSlots">Once switched on, the heating stays on for at least this many slots.</param>
public sealed record ThermalLimits(double Floor, double OnTemperature, double OffTemperature, int MinOnSlots);

/// <param name="On">The on/off decision for each slot.</param>
/// <param name="Cost">Sum of the prices of the "on" slots.</param>
/// <param name="Temperatures">Predicted temperature of each room (in input order) at the start of each slot, plus one entry for the end of the plan.</param>
public sealed record TemperatureSolveResult(IReadOnlyList<bool> On, decimal Cost, IReadOnlyList<double[]> Temperatures);

/// <summary>
/// Finds the cheapest on/off plan that keeps every room's predicted temperature at or above <see cref="ThermalLimits.Floor"/>,
/// using dynamic programming over the half-hour slots.
/// <list type="bullet">
/// <item><b>Model:</b> while off a room cools at its cooling rate (not below the off setpoint); while on it warms at its
/// heating rate up to the on setpoint, and once there the thermostat holds it.</item>
/// <item><b>Constraint:</b> a slot may be off only if every room stays at or above the floor. A room already below it
/// must be heated until it recovers.</item>
/// <item><b>End of the plan:</b> the heat left in the rooms is worth something, so each degree above the floor at the end
/// is credited at <c>storedHeatPrice</c> per slot of heating it saves later (for the room that would run out first).
/// Switching a slot from off to on gains the heating rate plus the cooling it avoids, so a degree saves
/// 1 ÷ ((heating + cooling) × ½ h) on-slots. Without this credit, every plan would end with the rooms cooled right
/// down to the floor.</item>
/// </list>
/// Temperatures are merged to the nearest 0.01°C to keep the search small. Ties are broken by fewest on/off switches.
/// </summary>
public static class TemperatureScheduleSolver
{
    public const double SlotHours = 0.5;
    private const double Resolution = 0.01;
    private const double Epsilon = 1e-9;

    private sealed record Node(decimal Cost, int Switches, string? Previous, bool On, double[] Temps, int OnRun);

    /// <summary>The temperature of a room after one slot.</summary>
    public static double Step(double temperature, bool on, RoomState room, ThermalLimits limits)
    {
        if (on)
        {
            return temperature < limits.OnTemperature
                ? Math.Min(limits.OnTemperature, temperature + room.HeatingPerHour * SlotHours)
                : Math.Max(limits.OnTemperature, temperature - room.CoolingPerHour * SlotHours);
        }

        return Math.Max(limits.OffTemperature, temperature - room.CoolingPerHour * SlotHours);
    }

    /// <summary>Value, in on-slots saved later, of the heat left above the floor at the end (for the room that runs out first).</summary>
    public static double StoredSlots(IReadOnlyList<double> temps, IReadOnlyList<RoomState> rooms, ThermalLimits limits) =>
        rooms.Count == 0 ? 0 : rooms.Select((r, i) =>
            (temps[i] - limits.Floor) / (Math.Max(0.01, r.HeatingPerHour + r.CoolingPerHour) * SlotHours)).Min();

    /// <param name="onRun">0 if the heating is off now, otherwise how many slots it has been on.</param>
    public static TemperatureSolveResult Solve(
        IReadOnlyList<decimal> prices, IReadOnlyList<RoomState> rooms, int onRun, ThermalLimits limits, decimal storedHeatPrice)
    {
        var n = prices.Count;
        var start = rooms.Select(r => r.Temperature).ToArray();
        if (n == 0)
        {
            return new TemperatureSolveResult([], 0m, [start]);
        }

        var startNode = new Node(0m, 0, null, onRun > 0, start, Math.Min(onRun, limits.MinOnSlots));
        var layers = new List<Dictionary<string, Node>>(n + 1) { new() { [Key(start, startNode.OnRun)] = startNode } };

        for (var i = 0; i < n; i++)
        {
            var next = new Dictionary<string, Node>();
            foreach (var (key, node) in layers[i])
            {
                foreach (var on in (ReadOnlySpan<bool>)[true, false])
                {
                    if (!on && node.OnRun > 0 && node.OnRun < limits.MinOnSlots)
                    {
                        continue; // still inside the minimum on-block
                    }

                    var temps = new double[rooms.Count];
                    var allowed = true;
                    for (var r = 0; r < rooms.Count; r++)
                    {
                        temps[r] = Step(node.Temps[r], on, rooms[r], limits);
                        allowed &= on || temps[r] >= limits.Floor - Epsilon;
                    }

                    if (!allowed)
                    {
                        continue;
                    }

                    var run = on ? Math.Min(node.OnRun + 1, limits.MinOnSlots) : 0;
                    var candidate = new Node(
                        node.Cost + (on ? prices[i] : 0m),
                        node.Switches + (on != node.On ? 1 : 0),
                        key, on, temps, run);
                    var nextKey = Key(temps, run);
                    if (!next.TryGetValue(nextKey, out var existing) || IsBetter(candidate, existing))
                    {
                        next[nextKey] = candidate;
                    }
                }
            }

            layers.Add(next);
        }

        var (bestKey, _, _) = layers[n]
            .Select(kv => (Key: kv.Key, Score: kv.Value.Cost - (decimal)StoredSlots(kv.Value.Temps, rooms, limits) * storedHeatPrice, kv.Value.Switches))
            .Aggregate((a, b) => b.Score < a.Score || (b.Score == a.Score && b.Switches < a.Switches) ? b : a);

        var result = new bool[n];
        var temperatures = new double[n + 1][];
        var current = bestKey;
        for (var i = n; i > 0; i--)
        {
            var node = layers[i][current];
            result[i - 1] = node.On;
            temperatures[i] = node.Temps;
            current = node.Previous!;
        }

        temperatures[0] = start;
        return new TemperatureSolveResult(result, layers[n][bestKey].Cost, temperatures);
    }

    private static bool IsBetter(Node a, Node b) =>
        a.Cost < b.Cost || (a.Cost == b.Cost && a.Switches < b.Switches);

    private static string Key(double[] temps, int onRun) =>
        string.Join(",", temps.Select(t => (long)Math.Round(t / Resolution))) + "|" + onRun;
}
