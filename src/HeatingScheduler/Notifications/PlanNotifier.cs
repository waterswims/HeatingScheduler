using System.Globalization;
using System.Text;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Notifications;

/// <summary>
/// Emails the plan whenever it changes: new prices, a tuning change or a restart. A change means a different on/off
/// decision for an upcoming slot, the plan reaching further ahead, or different rules. Slots dropping off the front
/// as time passes don't count. If sending fails, the next check tries again.
/// </summary>
public sealed class PlanNotifier(
    IEmailSender sender,
    IOptions<EmailOptions> emailOptions,
    IOptions<HeatingOptions> heatingOptions,
    ILogger<PlanNotifier> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlanSnapshot? _lastSent;

    /// <summary>Sends an email if <paramref name="snapshot"/> differs from the last plan emailed. Returns whether one was sent.</summary>
    /// <param name="reasons">Why the plan may have changed, e.g. "new Agile prices until Fri 23:00".</param>
    /// <param name="tuning">The latest tuning result, if any, for the measurements section.</param>
    public async Task<bool> NotifyIfChangedAsync(
        PlanSnapshot snapshot, IReadOnlyList<string> reasons, TuningResult? tuning, CancellationToken cancellationToken)
    {
        if (!emailOptions.Value.Enabled)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastSent is not null && !HasChanged(_lastSent, snapshot))
            {
                return false;
            }

            var (subject, body) = Compose(snapshot, _lastSent, reasons, tuning, heatingOptions.Value);
            try
            {
                await sender.SendAsync(subject, body, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to send the plan email; will try again at the next check");
                return false;
            }

            logger.LogInformation("Emailed the new heating plan to {To}", string.Join(", ", emailOptions.Value.To));
            _lastSent = snapshot;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the plan or rules differ, ignoring slots that have passed since <paramref name="previous"/>.</summary>
    public static bool HasChanged(PlanSnapshot previous, PlanSnapshot current)
    {
        if (current.Rules != previous.Rules)
        {
            return true;
        }

        var before = previous.Upcoming.ToDictionary(p => p.Start, p => p.State);
        var previousEnd = previous.Upcoming.Count > 0 ? previous.Upcoming[^1].Start : DateTimeOffset.MinValue;
        if (current.Upcoming.Count == 0)
        {
            return previous.Upcoming.Count > 0 && previousEnd > (current.CurrentSlot ?? DateTimeOffset.MinValue);
        }

        return current.Upcoming[^1].Start > previousEnd
            || current.Upcoming.Any(p => !before.TryGetValue(p.Start, out var state) || state != p.State);
    }

    public static (string Subject, string Body) Compose(
        PlanSnapshot plan, PlanSnapshot? previous, IReadOnlyList<string> reasons, TuningResult? tuning, HeatingOptions heating)
    {
        var inv = CultureInfo.InvariantCulture;
        var upcoming = plan.Upcoming;
        var on = upcoming.Where(p => p.State == HeatingState.On).ToList();
        var end = upcoming.Count > 0 ? upcoming[^1].Start + Slots.Length : (DateTimeOffset?)null;

        var subject = end is { } e
            ? string.Create(inv, $"Heating plan: on {Hours(on.Count)} of {Hours(upcoming.Count)}, to {Slots.Uk(e)}")
            : "Heating plan: no prices available";

        var b = new StringBuilder();
        b.AppendLine(reasons.Count > 0
            ? $"The heating plan has changed: {string.Join("; ", reasons)}."
            : "The heating plan has changed.");
        b.AppendLine();

        if (plan.CurrentSlot is { } slot && plan.Current is { } now)
        {
            b.AppendLine(string.Create(inv,
                $"Now: {(now.State == HeatingState.On ? "ON" : "off")} ({heating.TemperatureFor(now.State):0.#}°C) for the slot starting {Slots.Uk(slot)}{(now.IsFallback ? " (fallback: no prices)" : "")}."));
            b.AppendLine();
        }

        if (upcoming.Count == 0)
        {
            b.AppendLine("There is no plan: no Agile prices are known for the coming slots, so the fallback state is used.");
        }
        else
        {
            b.AppendLine("Plan (UK time):");
            foreach (var block in Blocks(upcoming))
            {
                var label = block[0].State == HeatingState.On ? "ON " : "off";
                b.AppendLine(string.Create(inv,
                    $"  {label}  {Slots.Uk(block[0].Start)} – {Slots.Uk(block[^1].Start + Slots.Length, "HH:mm"),-5}  {Hours(block.Count),6}   avg {block.Average(p => p.Price),5:0.00}p/kWh"));
            }

            b.AppendLine();
            b.AppendLine(string.Create(inv,
                $"On for {Hours(on.Count)} of {Hours(upcoming.Count)}, average price when on {(on.Count > 0 ? on.Average(p => p.Price) : 0m):0.00}p vs {upcoming.Average(p => p.Price):0.00}p/kWh overall."));
            b.AppendLine(string.Create(inv,
                $"Temperatures: on {heating.OnTemperature:0.#}°C, off {heating.OffTemperature:0.#}°C."));
        }

        b.AppendLine();
        b.AppendLine("Rules in force:");
        var r = plan.Rules;
        var was = previous?.Rules;
        b.AppendLine(string.Create(inv,
            $"  Make-up ratio (on time per off time): {r.OnToOffRatio:0.###}{(was is not null && was.OnToOffRatio != r.OnToOffRatio ? $"  (was {was.OnToOffRatio:0.###})" : "")}"));
        b.AppendLine(string.Create(inv,
            $"  Maximum off: {r.MaxOffSlots * 30} min{(was is not null && was.MaxOffSlots != r.MaxOffSlots ? $"  (was {was.MaxOffSlots * 30} min)" : "")}"));
        b.AppendLine(string.Create(inv, $"  Minimum on: {r.MinOnSlots * 30} min"));

        if (tuning is not null)
        {
            b.AppendLine();
            b.AppendLine(string.Create(inv,
                $"Tuning (from the last {ThermalTuner.History.TotalHours:0} hours of room temperatures): measured ratio {(tuning.OnToOffRatio?.ToString("0.###", inv) ?? "not measurable")}, max off {(tuning.MaxOffMinutes is int m ? $"{m} min" : "not measurable")}."));
            foreach (var room in tuning.Rooms)
            {
                b.AppendLine("  " + ThermalTuner.DescribeRoom(room));
            }
        }

        return (subject, b.ToString());
    }

    private static string Hours(int slots) => (slots / 2.0).ToString("0.#", CultureInfo.InvariantCulture) + "h";

    private static IEnumerable<List<PlannedSlot>> Blocks(IReadOnlyList<PlannedSlot> plan)
    {
        var block = new List<PlannedSlot>();
        foreach (var slot in plan)
        {
            if (block.Count > 0 && block[0].State != slot.State)
            {
                yield return block;
                block = [];
            }

            block.Add(slot);
        }

        if (block.Count > 0)
        {
            yield return block;
        }
    }
}
