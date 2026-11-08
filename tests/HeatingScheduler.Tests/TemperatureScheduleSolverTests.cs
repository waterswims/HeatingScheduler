using HeatingScheduler.Scheduling;

namespace HeatingScheduler.Tests;

public class TemperatureScheduleSolverTests
{
    private static readonly ThermalLimits Limits = new(Floor: 20.7, OnTemperature: 22.5, OffTemperature: 16, MinOnSlots: 2);

    private static RoomState Room(double temperature, double cooling = 0.2, double heating = 0.4, string key = "lobby") =>
        new(key, temperature, cooling, heating);

    /// <summary>Independent simulation of a plan, written from the rules rather than the solver's search.</summary>
    private static (bool Valid, decimal Objective, double[] End) Simulate(
        IReadOnlyList<bool> on, IReadOnlyList<decimal> prices, IReadOnlyList<RoomState> rooms, int onRun, ThermalLimits limits, decimal storedPrice)
    {
        var temps = rooms.Select(r => r.Temperature).ToArray();
        var run = onRun;
        var wasOn = onRun > 0;
        decimal cost = 0;
        for (var i = 0; i < on.Count; i++)
        {
            if (!on[i] && wasOn && run < limits.MinOnSlots)
            {
                return (false, 0, temps);
            }

            for (var r = 0; r < rooms.Count; r++)
            {
                var t = temps[r];
                temps[r] = on[i]
                    ? (t < limits.OnTemperature ? Math.Min(limits.OnTemperature, t + rooms[r].HeatingPerHour / 2) : Math.Max(limits.OnTemperature, t - rooms[r].CoolingPerHour / 2))
                    : Math.Max(limits.OffTemperature, t - rooms[r].CoolingPerHour / 2);
                if (!on[i] && temps[r] < limits.Floor - 1e-9)
                {
                    return (false, 0, temps);
                }
            }

            cost += on[i] ? prices[i] : 0m;
            run = on[i] ? (wasOn ? run + 1 : 1) : 0;
            wasOn = on[i];
        }

        var stored = rooms.Select((r, k) => (temps[k] - limits.Floor) / ((r.HeatingPerHour + r.CoolingPerHour) / 2)).DefaultIfEmpty(0).Min();
        return (true, cost - (decimal)stored * storedPrice, temps);
    }

    public static TheoryData<double, double, double, int> BruteForceCases()
    {
        var data = new TheoryData<double, double, double, int>();
        foreach (var start in new[] { 20.5, 20.9, 21.5, 22.5 })
        foreach (var (cool, heat) in new[] { (0.2, 0.4), (0.1, 0.2), (0.4, 0.2) })
        foreach (var onRun in new[] { 0, 1, 2 })
        {
            data.Add(start, cool, heat, onRun);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void Matches_brute_force_optimum(double start, double cooling, double heating, int onRun)
    {
        var rooms = new[] { Room(start, cooling, heating) };
        var random = new Random(HashCode.Combine(start, cooling, heating, onRun) & 0x7fffffff);
        const int n = 11;

        for (var trial = 0; trial < 4; trial++)
        {
            var prices = Enumerable.Range(0, n).Select(_ => (decimal)random.Next(-5, 60)).ToArray();
            var storedPrice = prices.Average();

            var best = decimal.MaxValue;
            for (var mask = 0; mask < 1 << n; mask++)
            {
                var plan = Enumerable.Range(0, n).Select(i => (mask >> i & 1) == 1).ToArray();
                var sim = Simulate(plan, prices, rooms, onRun, Limits, storedPrice);
                if (sim.Valid)
                {
                    best = Math.Min(best, sim.Objective);
                }
            }

            var result = TemperatureScheduleSolver.Solve(prices, rooms, onRun, Limits, storedPrice);
            var check = Simulate(result.On, prices, rooms, onRun, Limits, storedPrice);

            Assert.True(check.Valid, $"Invalid plan for prices [{string.Join(",", prices)}]");
            Assert.Equal(best, check.Objective, 6);
            Assert.Equal(check.End[0], result.Temperatures[^1][0], 6);
        }
    }

    [Fact]
    public void Keeps_every_room_above_the_floor()
    {
        // Two rooms; the draughty one forces earlier heating than the lobby alone would.
        var prices = Enumerable.Range(0, 16).Select(i => i < 8 ? 50m : 10m).ToArray();
        var lobby = Room(21.5, cooling: 0.1, heating: 0.4, key: "lobby");
        var draughty = Room(21.5, cooling: 0.4, heating: 0.4, key: "ensuite");

        var alone = TemperatureScheduleSolver.Solve(prices, [lobby], 2, Limits, 30m);
        var both = TemperatureScheduleSolver.Solve(prices, [lobby, draughty], 2, Limits, 30m);

        Assert.All(both.Temperatures.Skip(1), t => Assert.All(t, x => Assert.True(x >= Limits.Floor - 1e-9)));
        Assert.True(both.Cost > alone.Cost);
    }

    [Fact]
    public void Lets_the_room_cool_through_expensive_slots_and_heats_when_cheap()
    {
        // 21.5°C, cooling 0.2°C/h: it can stay off for 4 hours (8 slots) before reaching 20.7°C.
        var prices = new decimal[] { 60, 60, 60, 60, 60, 60, 60, 60, 5, 5, 5, 5 };
        var result = TemperatureScheduleSolver.Solve(prices, [Room(21.5)], 2, Limits, 30m);

        Assert.All(result.On.Take(8), on => Assert.False(on));
        Assert.All(result.On.Skip(8), on => Assert.True(on));
    }

    [Fact]
    public void Heats_straight_away_when_below_the_floor()
    {
        var prices = Enumerable.Repeat(40m, 6).ToArray();
        var result = TemperatureScheduleSolver.Solve(prices, [Room(20.4)], 0, Limits, 40m);

        Assert.True(result.On[0]);
        Assert.True(result.On[1]);
    }

    [Fact]
    public void Heating_stops_at_the_on_temperature()
    {
        Assert.Equal(22.5, TemperatureScheduleSolver.Step(22.4, on: true, Room(22.4), Limits), 9);
        Assert.Equal(22.7, TemperatureScheduleSolver.Step(22.8, on: true, Room(22.8), Limits), 9);  // above the setpoint: cools towards it
        Assert.Equal(22.5, TemperatureScheduleSolver.Step(22.55, on: true, Room(22.55), Limits), 9); // but not below it
        Assert.Equal(21.9, TemperatureScheduleSolver.Step(22.0, on: false, Room(22.0), Limits), 9);
        Assert.Equal(16.0, TemperatureScheduleSolver.Step(16.05, on: false, Room(16.05), Limits), 9);
    }

    [Fact]
    public void Stores_heat_when_cheaper_than_its_value_and_not_otherwise()
    {
        // Prices well below the value of stored heat (30p): heat up while cheap. Above it: stay off.
        var cheap = TemperatureScheduleSolver.Solve(Enumerable.Repeat(5m, 6).ToArray(), [Room(21.5)], 2, Limits, 30m);
        var dear = TemperatureScheduleSolver.Solve(Enumerable.Repeat(55m, 6).ToArray(), [Room(21.5)], 2, Limits, 30m);

        Assert.All(cheap.On, on => Assert.True(on));
        Assert.All(dear.On, on => Assert.False(on));
    }

    [Fact]
    public void Respects_the_minimum_on_block()
    {
        var prices = new decimal[] { 5, 90, 90, 90 };
        var result = TemperatureScheduleSolver.Solve(prices, [Room(20.9)], 1, Limits, 30m);

        Assert.True(result.On[0]); // already on for 1 slot of a 2-slot minimum
    }

    [Fact]
    public void Empty_plan_returns_start_temperatures()
    {
        var result = TemperatureScheduleSolver.Solve([], [Room(21.3)], 0, Limits, 30m);

        Assert.Empty(result.On);
        Assert.Equal(21.3, Assert.Single(Assert.Single(result.Temperatures)));
    }

    [Fact]
    public void Solves_a_long_plan_quickly()
    {
        var random = new Random(1);
        var prices = Enumerable.Range(0, 62).Select(_ => (decimal)random.Next(5, 60)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = TemperatureScheduleSolver.Solve(prices, [Room(21.8, 0.137, 0.168), Room(22.1, 0.18, 0.09, "ensuite")], 2, Limits, 30m);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.All(result.Temperatures.Skip(1), t => Assert.All(t, x => Assert.True(x >= Limits.Floor - 1e-9)));
    }
}
