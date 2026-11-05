using System.Text;
using HeatingScheduler.Octopus;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Scheduling;

public sealed record DesiredState(HeatingState State, bool IsFallback);

public sealed record PlannedSlot(DateTimeOffset Start, HeatingState State, decimal Price);

/// <summary>The current slot's decision, the plan after it, and the rules it was made with.</summary>
public sealed record PlanSnapshot(DateTimeOffset? CurrentSlot, DesiredState? Current, IReadOnlyList<PlannedSlot> Upcoming, RatioRules Rules);

public interface IHeatingSchedule
{
    /// <summary>The state for the slot containing <paramref name="utcNow"/>. Fixed once decided.</summary>
    DesiredState GetDesiredState(DateTimeOffset utcNow);

    void MarkFirstAttemptComplete();

    Task WaitForFirstAttemptAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken);
}

/// <summary>
/// Thread-safe store of known prices and committed slot decisions, shared by both background services.
/// <para>
/// When a slot starts, the solver plans over every known price (up to <see cref="MaxHorizon"/> ahead) from the actual
/// history, and the first slot of that plan is committed. Committed slots never change. Because the plan always runs to
/// the end of the known prices, it only changes when new prices arrive (about once a day), when the rules change, or
/// after a restart.
/// </para>
/// </summary>
public sealed class HeatingSchedule : IHeatingSchedule
{
    /// <summary>
    /// Furthest ahead the solver plans. In practice the plan ends where the published prices end: Octopus publishes the
    /// next day's prices at about 16:00, so the plan reaches 7–31 hours ahead.
    /// </summary>
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromHours(48);

    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    private readonly ScheduleOptions _options;
    private readonly ILogger<HeatingSchedule> _logger;
    private RatioRules _rules;
    private readonly Lock _lock = new();
    private readonly SortedDictionary<DateTimeOffset, decimal> _prices = [];

    // Value of each make-up slot still owed when the plan ends. Fixed when new prices arrive (the average of the known
    // prices then), so re-planning as slots pass gives the same plan rather than shifting with a moving average.
    private decimal? _owedSlotPrice;
    private readonly Dictionary<DateTimeOffset, DesiredState> _committed = [];
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DateTimeOffset? _nextSlot;          // first slot without a committed decision
    private HeatingHistory _history;            // history at the start of _nextSlot
    private IReadOnlyList<PlannedSlot> _plan = []; // current plan from _nextSlot onwards
    private IReadOnlyList<PlannedSlot> _lastLoggedPlan = [];

    public HeatingSchedule(IOptions<ScheduleOptions> options, ILogger<HeatingSchedule> logger)
    {
        _options = options.Value;
        _rules = new RatioRules(
            _options.OnToOffRatio,
            Slots.FromMinutes(_options.MinOnMinutes),
            Slots.FromMinutes(_options.MaxOffMinutes));
        _logger = logger;
    }

    /// <summary>
    /// History assumed after a restart, when the real history is unknown: the heating has been on for the
    /// minimum on-block with no heat left to make up, so the solver is free to switch off straight away.
    /// </summary>
    public HeatingHistory StartupHistory => HeatingHistory.On(_rules.MinOnSlots);

    /// <summary>The rules currently in force (initially from <see cref="ScheduleOptions"/>, then as tuned).</summary>
    public RatioRules Rules
    {
        get
        {
            lock (_lock)
            {
                return _rules;
            }
        }
    }

    /// <summary>
    /// Replaces the make-up ratio and maximum off period (e.g. from measured room temperatures) and re-plans.
    /// Decisions already committed are unaffected.
    /// </summary>
    public void UpdateRules(double onToOffRatio, int maxOffSlots)
    {
        lock (_lock)
        {
            _rules = _rules with { OnToOffRatio = onToOffRatio, MaxOffSlots = maxOffSlots };
            if (_nextSlot is { } next)
            {
                _plan = PlanFrom(next);
                LogPlanIfChanged();
            }
        }
    }

    /// <summary>The plan for slots after the current one (for logging and tests).</summary>
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

    /// <summary>A consistent snapshot of the current decision, the plan after it and the rules in force.</summary>
    public PlanSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            if (_nextSlot is not { } next)
            {
                return new PlanSnapshot(null, null, [], _rules);
            }

            var current = next - Slots.Length;
            return new PlanSnapshot(current, _committed.GetValueOrDefault(current), _plan, _rules);
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

    public void UpdatePrices(IEnumerable<AgileSlot> slots, DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            var endBefore = _prices.Count > 0 ? _prices.Keys.Last() : DateTimeOffset.MinValue;
            foreach (var slot in slots)
            {
                _prices[slot.ValidFrom] = slot.PriceIncVat;
            }

            var current = Slots.StartFor(utcNow);
            var future = _prices.Where(p => p.Key >= current).Select(p => p.Value).ToList();
            if (future.Count > 0 && (_owedSlotPrice is null || _prices.Keys.Last() > endBefore))
            {
                _owedSlotPrice = future.Average();
            }

            CommitUpTo(utcNow);
            _plan = PlanFrom(_nextSlot!.Value);
            LogPlanIfChanged();
        }
    }

    public DesiredState GetDesiredState(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            CommitUpTo(utcNow);

            // If the clock stepped back to before the first decision, use the most recent decision.
            return _committed.TryGetValue(Slots.StartFor(utcNow), out var decision)
                ? decision
                : _committed[_nextSlot!.Value - Slots.Length];
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

    /// <summary>Commits decisions for every slot up to and including the one containing <paramref name="utcNow"/>.</summary>
    private void CommitUpTo(DateTimeOffset utcNow)
    {
        var current = Slots.StartFor(utcNow);
        if (_nextSlot is null || _nextSlot < current - MaxCatchUp)
        {
            if (_nextSlot is not null)
            {
                _logger.LogWarning("No decisions made since {Slot}; restarting from the startup assumption", Slots.Uk(_nextSlot.Value));
            }

            _nextSlot = current;
            _history = StartupHistory;
            _logger.LogInformation("Starting schedule at {Slot} assuming the heating was {History}", Slots.Uk(current), _history);
        }

        var committedAny = false;
        while (_nextSlot <= current)
        {
            var slot = _nextSlot.Value;
            var plan = PlanFrom(slot);
            DesiredState decision;
            if (plan.Count == 0)
            {
                decision = new DesiredState(_options.FallbackState, IsFallback: true);
                if (decision.State == HeatingState.Off && !_rules.CanBeOff(_history))
                {
                    _logger.LogWarning("Fallback state Off at {Slot} breaks the scheduling rules ({History})", Slots.Uk(slot), _history);
                }
            }
            else
            {
                decision = new DesiredState(plan[0].State, IsFallback: false);
            }

            _committed[slot] = decision;
            _history = _history.Advance(decision.State == HeatingState.On);
            _nextSlot = slot + Slots.Length;
            _plan = plan.Count > 0 ? plan.Skip(1).ToList() : [];
            committedAny = true;
            _logger.LogDebug("Committed {Slot}: {State}{Fallback}; now {History}",
                Slots.Uk(slot), decision.State, decision.IsFallback ? " (fallback)" : "", _history);
        }

        if (committedAny)
        {
            foreach (var old in _committed.Keys.Where(k => k < current - MaxCatchUp).ToList())
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

    /// <summary>Solves the schedule from <paramref name="from"/> over the known prices within the horizon, starting from the current history.</summary>
    private List<PlannedSlot> PlanFrom(DateTimeOffset from)
    {
        var end = from + MaxHorizon;
        var starts = new List<DateTimeOffset>();
        var prices = new List<decimal>();
        for (var t = from; t < end && _prices.TryGetValue(t, out var price); t += Slots.Length)
        {
            starts.Add(t);
            prices.Add(price);
        }

        var result = RatioScheduleSolver.Solve(prices, _history, _rules, _owedSlotPrice);
        return starts
            .Select((s, i) => new PlannedSlot(s, result.On[i] ? HeatingState.On : HeatingState.Off, prices[i]))
            .ToList();
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
        _logger.LogInformation(
            "Plan to {End} (UK): {Blocks}. On {OnHours:0.#}h of {TotalHours:0.#}h, avg on price {OnPrice:F2}p vs {AvgPrice:F2}p/kWh",
            Slots.Uk(_plan[^1].Start + Slots.Length),
            DescribeBlocks(_plan),
            on.Count / 2.0,
            _plan.Count / 2.0,
            on.Count > 0 ? on.Average(p => p.Price) : 0m,
            _plan.Average(p => p.Price));
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
                .Append(Slots.Uk(plan[j].Start + Slots.Length, "HH:mm"));
            i = j + 1;
        }

        return sb.ToString();
    }
}
