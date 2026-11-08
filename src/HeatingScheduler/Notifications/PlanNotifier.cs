using System.Globalization;
using System.Text;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Notifications;

/// <summary>
/// Emails the heating plan when <see cref="PriceFetchService"/> asks it to (when new prices arrive). Each email has three
/// parts: the plan sent in the previous email, what actually happened since then (each slot's decision, price and the
/// rooms' measured temperature), and the new plan.
/// </summary>
public sealed class PlanNotifier(
    IEmailSender sender,
    HeatingSchedule schedule,
    IOptions<EmailOptions> emailOptions,
    IOptions<HeatingOptions> heatingOptions,
    ILogger<PlanNotifier> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlanSnapshot? _lastSent;

    /// <summary>
    /// Emails the current plan. Returns true when done (sent, or email is turned off) and false if sending failed, so
    /// the caller can try again later.
    /// </summary>
    /// <param name="reasons">Why the email is being sent, e.g. "new Agile prices until Fri 23:00".</param>
    /// <param name="tuning">The latest tuning result, if any, for the measurements section.</param>
    public async Task<bool> NotifyAsync(IReadOnlyList<string> reasons, TuningResult? tuning, CancellationToken cancellationToken)
    {
        if (!emailOptions.Value.Enabled)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = schedule.GetSnapshot();
            var happened = _lastSent?.CurrentSlot is { } since && current.CurrentSlot is { } now
                ? schedule.History(since, now)
                : [];
            var (subject, body) = Compose(_lastSent, happened, current, reasons, tuning, heatingOptions.Value);
            try
            {
                await sender.SendAsync(subject, body, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to send the plan email; will try again at the next price check");
                return false;
            }

            logger.LogInformation("Emailed the heating plan to {To}", string.Join(", ", emailOptions.Value.To));
            _lastSent = current;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static (string Subject, string Body) Compose(
        PlanSnapshot? previous, IReadOnlyList<CommittedSlot> happened, PlanSnapshot plan,
        IReadOnlyList<string> reasons, TuningResult? tuning, HeatingOptions heating)
    {
        var inv = CultureInfo.InvariantCulture;
        var upcoming = plan.Upcoming;
        var on = upcoming.Where(p => p.State == HeatingState.On).ToList();
        var end = upcoming.Count > 0 ? upcoming[^1].Start + Slots.Length : (DateTimeOffset?)null;

        var subject = end is { } e
            ? string.Create(inv, $"Heating plan: on {Hours(on.Count)} of {Hours(upcoming.Count)}, to {Slots.Uk(e)}")
            : "Heating plan: no plan available";

        var b = new StringBuilder();
        if (reasons.Count > 0)
        {
            b.AppendLine(Capitalise(string.Join("; ", reasons)) + ".");
            b.AppendLine();
        }

        // 1. The plan in the previous email
        if (previous?.CurrentSlot is { } sentSlot)
        {
            b.AppendLine($"PLAN IN THE LAST EMAIL (sent during the slot starting {Slots.Uk(sentSlot)})");
            var sent = PlannedWithCurrent(previous);
            if (sent.Count == 0)
            {
                b.AppendLine("  There was no plan.");
            }

            foreach (var block in Blocks(sent, s => s.State))
            {
                b.AppendLine(PlannedLine(block, inv));
            }
        }
        else
        {
            b.AppendLine("PLAN IN THE LAST EMAIL");
            b.AppendLine("  None: this is the first email since the service started.");
        }

        b.AppendLine();

        // 2. What actually happened since
        if (previous?.CurrentSlot is { } since && plan.CurrentSlot is { } nowSlot)
        {
            b.AppendLine($"WHAT HAPPENED ({Slots.Uk(since)} – {EndTime(since, nowSlot)}), with the lowest measured room temperature at the start and end of each block");
            if (happened.Count == 0)
            {
                b.AppendLine("  No slots were decided in that time.");
            }

            var blocks = Blocks(happened, c => (c.Decision.State, c.Decision.IsFallback)).ToList();
            for (var k = 0; k < blocks.Count; k++)
            {
                var block = blocks[k];
                var endTemps = k + 1 < blocks.Count ? blocks[k + 1][0].MeasuredTemperatures : CurrentTemperatures(plan);
                var priced = block.Where(c => c.Price is not null).ToList();
                var label = block[0].Decision.State == HeatingState.On ? "ON " : "off";
                b.AppendLine(string.Create(inv,
                    $"  {label}  {Slots.Uk(block[0].Start)} – {EndTime(block[0].Start, block[^1].Start + Slots.Length),-9}  {Hours(block.Count),6}   avg {(priced.Count > 0 ? priced.Average(c => c.Price!.Value).ToString("0.00", inv) : "  –  "),5}p/kWh   {Temp(block[0].MeasuredTemperatures)} → {Temp(endTemps)}{(block[0].Decision.IsFallback ? "   (fallback: no plan)" : "")}"));
            }

            var actualOn = happened.Where(c => c.Decision.State == HeatingState.On).ToList();
            var planned = PlannedWithCurrent(previous).Where(p => p.Start >= since && p.Start < nowSlot).ToList();
            var plannedOn = planned.Where(p => p.State == HeatingState.On).ToList();
            if (happened.Count > 0)
            {
                b.AppendLine(string.Create(inv,
                    $"  On for {Hours(actualOn.Count)}{AveragePrice(actualOn.Select(c => c.Price))}. The last email planned {Hours(plannedOn.Count)}{AveragePrice(plannedOn.Select(p => (decimal?)p.Price))} for the same period."));
            }
        }
        else
        {
            b.AppendLine("WHAT HAPPENED");
            b.AppendLine("  Nothing to compare with yet.");
        }

        b.AppendLine();

        // 3. The new plan
        b.AppendLine("NEW PLAN, with the lowest predicted room temperature at the end of each block");
        if (plan.CurrentSlot is { } slot && plan.Current is { } now)
        {
            b.AppendLine(string.Create(inv,
                $"  Now: {(now.State == HeatingState.On ? "ON" : "off")} ({heating.TemperatureFor(now.State):0.#}°C) for the slot starting {Slots.Uk(slot)}{(now.IsFallback ? " (fallback: no prices or no recent room temperature)" : "")}."));
        }

        if (upcoming.Count == 0)
        {
            b.AppendLine("  There is no plan (no Agile prices for the coming slots, or no recent room temperature), so the fallback state is used.");
        }
        else
        {
            foreach (var block in Blocks(upcoming, s => s.State))
            {
                b.AppendLine(PlannedLine(block, inv));
            }

            var lowest = upcoming.MinBy(p => p.LowestTemperature)!;
            b.AppendLine(string.Create(inv,
                $"  On for {Hours(on.Count)} of {Hours(upcoming.Count)}{AveragePrice(on.Select(p => (decimal?)p.Price))}, against {upcoming.Average(p => p.Price):0.00}p/kWh overall."));
            b.AppendLine(string.Create(inv,
                $"  Lowest predicted temperature: {lowest.LowestTemperature:0.0}°C at {Slots.Uk(lowest.Start + Slots.Length)} (the plan keeps rooms at or above {plan.Floor:0.0}°C)."));
        }

        b.AppendLine();
        b.AppendLine(string.Create(inv, $"Setpoints: on {heating.OnTemperature:0.#}°C, off {heating.OffTemperature:0.#}°C."));
        b.AppendLine("Rooms the plan keeps warm:");
        var before = previous?.Rooms.ToDictionary(r => r.Key);
        foreach (var r in plan.Rooms)
        {
            var was = before?.GetValueOrDefault(r.Key);
            string Rate(double value, double? old) =>
                string.Create(inv, $"{value:0.000}°C/h") + (old is double o && Math.Abs(o - value) > 0.0005 ? string.Create(inv, $" (was {o:0.000})") : "");
            b.AppendLine(string.Create(inv,
                $"  {r.Name}: now {(r.Temperature is double t ? t.ToString("0.0", inv) + "°C" : "unknown")}, cooling {Rate(r.CoolingPerHour, was?.CoolingPerHour)}, heating {Rate(r.HeatingPerHour, was?.HeatingPerHour)}"));
        }

        if (tuning is not null)
        {
            b.AppendLine();
            b.AppendLine(string.Create(inv, $"Measured from the last {ThermalTuner.History.TotalHours:0} hours of room temperatures:"));
            foreach (var room in tuning.Rooms)
            {
                b.AppendLine("  " + ThermalTuner.DescribeRoom(room));
            }
        }

        return (subject, b.ToString());
    }

    /// <summary>The previous email's plan including the slot that was current when it was sent.</summary>
    private static List<PlannedSlot> PlannedWithCurrent(PlanSnapshot snapshot)
    {
        var slots = new List<PlannedSlot>();
        if (snapshot.CurrentSlot is { } c && snapshot.Current is { IsFallback: false } d && snapshot.CurrentPrice is { } price)
        {
            // The first upcoming slot's start temperatures are the prediction for the end of the current one.
            var end = snapshot.Upcoming.Count > 0 ? snapshot.Upcoming[0].StartTemperatures : new Dictionary<string, double>();
            slots.Add(new PlannedSlot(c, d.State, price, end, end.Values.DefaultIfEmpty(double.NaN).Min()));
        }

        slots.AddRange(snapshot.Upcoming);
        return slots;
    }

    private static string PlannedLine(List<PlannedSlot> block, IFormatProvider inv)
    {
        var label = block[0].State == HeatingState.On ? "ON " : "off";
        return string.Create(inv,
            $"  {label}  {Slots.Uk(block[0].Start)} – {EndTime(block[0].Start, block[^1].Start + Slots.Length),-9}  {Hours(block.Count),6}   avg {block.Average(p => p.Price),5:0.00}p/kWh   → {block[^1].LowestTemperature:0.0}°C");
    }

    /// <summary>The end time, with its day only when it isn't the same day as <paramref name="start"/> (UK time).</summary>
    private static string EndTime(DateTimeOffset start, DateTimeOffset end) =>
        Slots.Uk(start, "yyyy-MM-dd") == Slots.Uk(end, "yyyy-MM-dd") ? Slots.Uk(end, "HH:mm") : Slots.Uk(end);

    private static IReadOnlyDictionary<string, double> CurrentTemperatures(PlanSnapshot plan) =>
        plan.Rooms.Where(r => r.Temperature is not null).ToDictionary(r => r.Key, r => r.Temperature!.Value);

    private static string Temp(IReadOnlyDictionary<string, double> temps) =>
        temps.Count == 0 ? "?" : temps.Values.Min().ToString("0.0", CultureInfo.InvariantCulture) + "°C";

    private static string AveragePrice(IEnumerable<decimal?> prices)
    {
        var known = prices.Where(p => p is not null).Select(p => p!.Value).ToList();
        return known.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" at an average of {known.Average():0.00}p");
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Hours(int slots) => (slots / 2.0).ToString("0.#", CultureInfo.InvariantCulture) + "h";

    private static IEnumerable<List<T>> Blocks<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key)
    {
        var block = new List<T>();
        foreach (var item in items)
        {
            if (block.Count > 0 && !EqualityComparer<TKey>.Default.Equals(key(block[0]), key(item)))
            {
                yield return block;
                block = [];
            }

            block.Add(item);
        }

        if (block.Count > 0)
        {
            yield return block;
        }
    }
}
