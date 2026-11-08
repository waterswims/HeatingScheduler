using System.Globalization;
using System.Text;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Scheduling;

public sealed record DesiredState(HeatingState State, bool IsFallback);

/// <param name="StartTemperatures">Predicted temperature of each tracked room (by key) at the start of the slot.</param>
/// <param name="LowestTemperature">Lowest predicted temperature of any tracked room at the end of the slot.</param>
public sealed record PlannedSlot(
    DateTimeOffset Start, HeatingState State, decimal Price,
    IReadOnlyDictionary<string, double> StartTemperatures, double LowestTemperature);

/// <summary>A slot whose decision has been made: what was decided, its price, and the tracked rooms' measured temperatures when it started.</summary>
public sealed record CommittedSlot(DateTimeOffset Start, DesiredState Decision, decimal? Price, IReadOnlyDictionary<string, double> MeasuredTemperatures);

/// <summary>What the plan knows about a tracked room: its rates, and its last measured temperature.</summary>
public sealed record RoomModel(string Key, string Name, double CoolingPerHour, double HeatingPerHour, double? Temperature, DateTimeOffset? MeasuredAt);

/// <summary>The current slot's decision and price, the plan after it, and the room model it was made with.</summary>
public sealed record PlanSnapshot(
    DateTimeOffset? CurrentSlot, DesiredState? Current, decimal? CurrentPrice,
    IReadOnlyList<PlannedSlot> Upcoming, IReadOnlyList<RoomModel> Rooms, double Floor);

public sealed record RoomTemperature(string Key, string Name, double Temperature, DateTimeOffset At);

/// <summary>Measured rates for a room; a null rate leaves the current one in place.</summary>
public sealed record RoomRates(string Key, string Name, double? CoolingPerHour, double? HeatingPerHour);

public interface IHeatingSchedule
{
    /// <summary>Records the latest measured temperatures of the tracked rooms.</summary>
    void UpdateTemperatures(IEnumerable<RoomTemperature> temperatures);

    /// <summary>The state for the slot containing <paramref name="utcNow"/>. Fixed once decided.</summary>
    DesiredState GetDesiredState(DateTimeOffset utcNow);

    void MarkFirstAttemptComplete();

    Task WaitForFirstAttemptAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken);
}

/// <summary>
/// Thread-safe store of prices, room temperatures and rates, and committed slot decisions, shared by the services.
/// <para>
/// The plan is the cheapest on/off sequence over every known price that keeps each tracked room's predicted temperature
/// above the minimum plus the safety margin (<see cref="TemperatureScheduleSolver"/>). When a slot starts, the planned
/// decision is committed and never changes. The plan is re-made from the measured room temperatures at the start of
/// every hour, so errors in the model are corrected, and also when the prices or the rates change, or after a restart.
/// </para>
/// </summary>
public sealed class HeatingSchedule : IHeatingSchedule
{
    /// <summary>
    /// Furthest ahead the solver plans. In practice the plan ends where the published prices end: Octopus publishes the
    /// next day's prices at about 16:00, so the plan reaches 7–31 hours ahead.
    /// </summary>
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromHours(48);

    /// <summary>How long committed slots are kept, for the "what happened" part of the email.</summary>
    public static readonly TimeSpan HistoryKept = TimeSpan.FromHours(72);

    /// <summary>Temperatures older than this are not used to plan.</summary>
    public static readonly TimeSpan MaxTemperatureAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    private readonly ScheduleOptions _options;
    private readonly ThermalLimits _limits;
    private readonly ILogger<HeatingSchedule> _logger;
    private readonly Lock _lock = new();
    private readonly SortedDictionary<DateTimeOffset, decimal> _prices = [];
    private readonly Dictionary<string, RoomTemperature> _temperatures = [];
    private readonly Dictionary<string, RoomRates> _rates = [];
    private readonly SortedDictionary<DateTimeOffset, CommittedSlot> _committed = [];
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Value of each slot of heat left in the rooms at the end of the plan. Fixed when new prices arrive (the average of
    // the known prices then), so re-planning as slots pass doesn't shift with a moving average.
    private decimal _storedHeatPrice;

    private DateTimeOffset? _nextSlot;               // first slot without a committed decision
    private int _onRun;                              // slots the heating has been on at the start of _nextSlot (0 = off)
    private IReadOnlyList<PlannedSlot> _plan = [];   // plan from _nextSlot onwards
    private IReadOnlyList<PlannedSlot> _lastLoggedPlan = [];
    private DateTimeOffset _now = DateTimeOffset.MinValue;

    public HeatingSchedule(IOptions<ScheduleOptions> options, IOptions<HeatingOptions> heating, ILogger<HeatingSchedule> logger)
    {
        _options = options.Value;
        _limits = new ThermalLimits(
            _options.Floor, heating.Value.OnTemperature, heating.Value.OffTemperature, Slots.FromMinutes(_options.MinOnMinutes));
        _logger = logger;
    }

    public ThermalLimits Limits => _limits;

    /// <summary>The plan for slots after the current one.</summary>
    public IReadOnlyList<PlannedSlot> Plan
    {
        get
        {
            lock (_lock)
            {
                return _plan;
            }
        }
    }

    /// <summary>The room model: every room with a temperature or measured rates.</summary>
    public IReadOnlyList<RoomModel> Rooms
    {
        get
        {
            lock (_lock)
            {
                return RoomModels();
            }
        }
    }

    /// <summary>The committed slots starting in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    public IReadOnlyList<CommittedSlot> History(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return _committed.Values.Where(c => c.Start >= from && c.Start < to).ToList();
        }
    }

    /// <summary>A consistent snapshot of the current decision, the plan after it and the room model.</summary>
    public PlanSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            if (_nextSlot is not { } next)
            {
                return new PlanSnapshot(null, null, null, [], RoomModels(), _limits.Floor);
            }

            var current = next - Slots.Length;
            var committed = _committed.GetValueOrDefault(current);
            return new PlanSnapshot(current, committed?.Decision, committed?.Price, _plan, RoomModels(), _limits.Floor);
        }
    }

    /// <summary>End of the unbroken run of known prices starting at <paramref name="fromSlot"/>.</summary>
    public DateTimeOffset PricesKnownUntil(DateTimeOffset fromSlot)
    {
        lock (_lock)
        {
            var t = fromSlot;
            while (_prices.ContainsKey(t))
            {
                t += Slots.Length;
            }

            return t;
        }
    }

    public void UpdateTemperatures(IEnumerable<RoomTemperature> temperatures)
    {
        lock (_lock)
        {
            foreach (var t in temperatures)
            {
                _temperatures[t.Key] = t;
                _now = t.At > _now ? t.At : _now;
            }
        }
    }

    /// <summary>Applies measured rates (null rates leave the current value) and re-makes the plan.</summary>
    public void UpdateRates(IEnumerable<RoomRates> rates)
    {
        lock (_lock)
        {
            foreach (var r in rates)
            {
                var current = _rates.GetValueOrDefault(r.Key);
                _rates[r.Key] = new RoomRates(r.Key, r.Name, r.CoolingPerHour ?? current?.CoolingPerHour, r.HeatingPerHour ?? current?.HeatingPerHour);
            }

            Replan();
        }
    }

    public void UpdatePrices(IEnumerable<AgileSlot> slots, DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            _now = utcNow > _now ? utcNow : _now;
            var changed = false;
            foreach (var slot in slots)
            {
                changed |= !_prices.TryGetValue(slot.ValidFrom, out var existing) || existing != slot.PriceIncVat;
                _prices[slot.ValidFrom] = slot.PriceIncVat;
            }

            var current = Slots.StartFor(utcNow);
            var future = _prices.Where(p => p.Key >= current).Select(p => p.Value).ToList();
            if (changed && future.Count > 0)
            {
                _storedHeatPrice = future.Average();
            }

            CommitUpTo(utcNow);

            // Only when the prices changed: re-planning from a measurement taken part-way through a slot would otherwise
            // nudge the plan, and send an email, every half hour.
            if (changed)
            {
                Replan();
            }
        }
    }

    public DesiredState GetDesiredState(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            _now = utcNow > _now ? utcNow : _now;
            CommitUpTo(utcNow);

            // If the clock stepped back to before the first decision, use the most recent decision.
            return _committed.TryGetValue(Slots.StartFor(utcNow), out var slot)
                ? slot.Decision
                : _committed[_nextSlot!.Value - Slots.Length].Decision;
        }
    }

    public void MarkFirstAttemptComplete() => _firstAttempt.TrySetResult();

    /// <summary>Waits for the first price fetch attempt, up to <paramref name="timeout"/>, so the controller doesn't act on the fallback state at startup.</summary>
    public async Task WaitForFirstAttemptAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            await _firstAttempt.Task.WaitAsync(timeout, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Proceed with whatever is available (fallback state).
        }
    }

    private void Replan()
    {
        if (_nextSlot is { } next)
        {
            var plan = PlanFrom(next);
            if (plan.Count > 0 || _plan.Count == 0)
            {
                _plan = plan;
            }

            LogPlanIfChanged();
        }
    }

    /// <summary>Commits decisions for every slot up to and including the one containing <paramref name="utcNow"/>.</summary>
    private void CommitUpTo(DateTimeOffset utcNow)
    {
        var current = Slots.StartFor(utcNow);
        if (_nextSlot is null || _nextSlot < current - MaxCatchUp)
        {
            if (_nextSlot is not null)
            {
                _logger.LogWarning("No decisions made since {Slot}; starting afresh", Slots.Uk(_nextSlot.Value));
            }

            // After a restart the heating's recent history is unknown: treat any minimum on-block as done.
            _nextSlot = current;
            _onRun = _limits.MinOnSlots;
            _plan = [];
            _logger.LogInformation("Starting schedule at {Slot}", Slots.Uk(current));
        }

        var committedAny = false;
        while (_nextSlot <= current)
        {
            var slot = _nextSlot.Value;
            var planned = _plan.Count > 0 && _plan[0].Start == slot ? _plan[0] : null;
            if (planned is null || slot.UtcDateTime.Minute == 0)
            {
                // On the hour, re-make the plan from the measured temperatures. Without fresh ones, keep following the plan.
                var plan = PlanFrom(slot);
                if (plan.Count > 0 || planned is null)
                {
                    _plan = plan;
                }
            }

            DesiredState decision;
            if (_plan.Count == 0)
            {
                decision = new DesiredState(_options.FallbackState, IsFallback: true);
            }
            else
            {
                decision = new DesiredState(_plan[0].State, IsFallback: false);
                _plan = _plan.Skip(1).ToList();
            }

            _committed[slot] = new CommittedSlot(
                slot, decision, _prices.TryGetValue(slot, out var price) ? price : null,
                FreshTemperatures().ToDictionary(kv => kv.Key, kv => kv.Value.Temperature));
            _onRun = decision.State == HeatingState.On ? Math.Min(_onRun + 1, _limits.MinOnSlots) : 0;
            _nextSlot = slot + Slots.Length;
            committedAny = true;
            _logger.LogDebug("Committed {Slot}: {State}{Fallback}", Slots.Uk(slot), decision.State, decision.IsFallback ? " (fallback)" : "");
        }

        if (committedAny)
        {
            foreach (var old in _committed.Keys.Where(k => k < current - HistoryKept).ToList())
            {
                _committed.Remove(old);
            }

            foreach (var old in _prices.Keys.TakeWhile(k => k < current).ToList())
            {
                _prices.Remove(old);
            }

            LogPlanIfChanged();
        }
    }

    private Dictionary<string, RoomTemperature> FreshTemperatures() =>
        _temperatures.Values.Where(t => t.At >= _now - MaxTemperatureAge).ToDictionary(t => t.Key);

    private List<RoomModel> RoomModels() =>
        _temperatures.Keys.Union(_rates.Keys).Order().Select(key =>
        {
            var t = _temperatures.GetValueOrDefault(key);
            var r = _rates.GetValueOrDefault(key);
            return new RoomModel(
                key, t?.Name ?? r!.Name,
                r?.CoolingPerHour ?? _options.CoolingRate,
                r?.HeatingPerHour ?? _options.HeatingRate,
                t?.Temperature, t?.At);
        }).ToList();

    /// <summary>Plans from <paramref name="from"/> over the known prices, starting from the rooms' fresh measured temperatures.</summary>
    private List<PlannedSlot> PlanFrom(DateTimeOffset from)
    {
        var fresh = FreshTemperatures();
        if (fresh.Count == 0)
        {
            return [];
        }

        var starts = new List<DateTimeOffset>();
        var prices = new List<decimal>();
        for (var t = from; t < from + MaxHorizon && _prices.TryGetValue(t, out var price); t += Slots.Length)
        {
            starts.Add(t);
            prices.Add(price);
        }

        if (prices.Count == 0)
        {
            return [];
        }

        // Bring each measurement forward to the start of the plan, using the decision for the slot in between.
        var models = RoomModels().Where(m => fresh.ContainsKey(m.Key)).ToList();
        var between = _committed.GetValueOrDefault(from - Slots.Length)?.Decision;
        var rooms = models.Select(m =>
        {
            var t = fresh[m.Key];
            var hours = (from - t.At).TotalHours;
            var temperature = t.Temperature;
            if (hours > 0 && between is not null)
            {
                var room = new RoomState(m.Key, temperature, m.CoolingPerHour, m.HeatingPerHour);
                var full = TemperatureScheduleSolver.Step(temperature, between.State == HeatingState.On, room, _limits);
                temperature += (full - temperature) * Math.Min(1, hours / TemperatureScheduleSolver.SlotHours);
            }

            return new RoomState(m.Key, temperature, m.CoolingPerHour, m.HeatingPerHour);
        }).ToList();
        var result = TemperatureScheduleSolver.Solve(prices, rooms, _onRun, _limits, _storedHeatPrice);

        return starts.Select((s, i) => new PlannedSlot(
            s,
            result.On[i] ? HeatingState.On : HeatingState.Off,
            prices[i],
            rooms.Select((r, k) => (r.Key, T: result.Temperatures[i][k])).ToDictionary(x => x.Key, x => x.T),
            result.Temperatures[i + 1].Min())).ToList();
    }

    /// <summary>Logs the plan when it differs from the last logged one (ignoring slots that have since passed).</summary>
    private void LogPlanIfChanged()
    {
        var previous = _lastLoggedPlan.ToDictionary(p => p.Start, p => p.State);
        var unchanged = _plan.Count > 0
            && _plan.All(p => previous.TryGetValue(p.Start, out var s) && s == p.State);
        if (unchanged || _plan.Count == 0)
        {
            return;
        }

        _lastLoggedPlan = _plan;
        var on = _plan.Where(p => p.State == HeatingState.On).ToList();
        var lowest = _plan.MinBy(p => p.LowestTemperature)!;
        _logger.LogInformation(
            "Plan to {End} (UK): {Blocks}. On {OnHours:0.#}h of {TotalHours:0.#}h, avg on price {OnPrice:F2}p vs {AvgPrice:F2}p/kWh; lowest predicted {Lowest:0.00}°C at {LowestAt} (floor {Floor:0.0}°C)",
            Slots.Uk(_plan[^1].Start + Slots.Length),
            DescribeBlocks(_plan),
            on.Count / 2.0,
            _plan.Count / 2.0,
            on.Count > 0 ? on.Average(p => p.Price) : 0m,
            _plan.Average(p => p.Price),
            lowest.LowestTemperature,
            Slots.Uk(lowest.Start + Slots.Length),
            _limits.Floor);
    }

    public static string DescribeBlocks(IReadOnlyList<PlannedSlot> plan)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < plan.Count)
        {
            var j = i;
            while (j + 1 < plan.Count && plan[j + 1].State == plan[i].State)
            {
                j++;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(plan[i].State == HeatingState.On ? "ON " : "off ")
                .Append(Slots.Uk(plan[i].Start))
                .Append('–')
                .Append(Slots.Uk(plan[j].Start + Slots.Length, "HH:mm"))
                .Append(string.Create(CultureInfo.InvariantCulture, $" ({plan[j].LowestTemperature:0.0}°C)"));
            i = j + 1;
        }

        return sb.ToString();
    }
}
