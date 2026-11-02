using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Scheduling;

public sealed record DesiredState(HeatingState State, bool IsFallback);

/// <summary>Thread-safe in-memory store of computed day schedules, shared by both background services.</summary>
public sealed class HeatingSchedule(IOptions<ScheduleOptions> options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<AgileDay, DaySchedule> _days = [];
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals that the first price fetch has been attempted (whether or not it succeeded).</summary>
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

    public bool HasDay(AgileDay day)
    {
        lock (_lock)
        {
            return _days.ContainsKey(day);
        }
    }

    public void SetDay(DaySchedule schedule)
    {
        lock (_lock)
        {
            _days[schedule.Day] = schedule;
        }
    }

    /// <summary>Removes schedules for days that ended before <paramref name="utcNow"/>.</summary>
    public void RemoveDaysBefore(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            foreach (var day in _days.Keys.Where(d => d.EndUtc <= utcNow).ToList())
            {
                _days.Remove(day);
            }
        }
    }

    public DesiredState GetDesiredState(DateTimeOffset utcNow)
    {
        var day = AgileDay.Containing(utcNow);
        DaySchedule? schedule;
        lock (_lock)
        {
            _days.TryGetValue(day, out schedule);
        }

        if (schedule is null)
        {
            return new DesiredState(options.Value.FallbackState, IsFallback: true);
        }

        var state = schedule.IsOn(AgileDay.SlotStartFor(utcNow)) ? HeatingState.On : HeatingState.Off;
        return new DesiredState(state, IsFallback: false);
    }
}
