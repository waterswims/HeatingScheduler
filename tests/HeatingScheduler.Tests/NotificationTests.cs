using HeatingScheduler.Notifications;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatingScheduler.Tests;

public class PlanNotifierTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-05T16:00:00Z");
    private static readonly RatioRules Rules = new(1.0, 2, 20);
    private static readonly HeatingOptions Heating = new() { OnTemperature = 23, OffTemperature = 16 };

    private sealed class FakeEmail : IEmailSender
    {
        public List<(string Subject, string Body)> Sent { get; } = [];
        public int FailuresLeft { get; set; }

        public Task SendAsync(string subject, string body, CancellationToken cancellationToken)
        {
            if (FailuresLeft-- > 0)
            {
                throw new IOException("SMTP unavailable");
            }

            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>A snapshot whose current slot is T0 + <paramref name="offset"/> slots, with the given upcoming states ('1' = on).</summary>
    private static PlanSnapshot Snapshot(int offset, string states, RatioRules? rules = null)
    {
        var current = T0 + offset * Slots.Length;
        var upcoming = states.Select((c, i) => new PlannedSlot(current + (i + 1) * Slots.Length, c == '1' ? HeatingState.On : HeatingState.Off, 20m + i)).ToList();
        return new PlanSnapshot(current, new DesiredState(HeatingState.Off, false), upcoming, rules ?? Rules);
    }

    private static PlanNotifier Create(FakeEmail email, bool enabled = true) => new(
        email,
        TestHelpers.Opt(new EmailOptions { Enabled = enabled, To = ["me@example.com"], SmtpHost = "smtp.example.com", From = "app@example.com" }),
        TestHelpers.Opt(Heating),
        NullLogger<PlanNotifier>.Instance);

    [Fact]
    public void Slots_passing_is_not_a_change()
    {
        // One slot later, the same plan minus the slot that has started.
        Assert.False(PlanNotifier.HasChanged(Snapshot(0, "0011100"), Snapshot(1, "011100")));
    }

    [Fact]
    public void Different_decision_is_a_change() =>
        Assert.True(PlanNotifier.HasChanged(Snapshot(0, "0011100"), Snapshot(0, "0001110")));

    [Fact]
    public void Plan_reaching_further_ahead_is_a_change() =>
        Assert.True(PlanNotifier.HasChanged(Snapshot(0, "0011100"), Snapshot(0, "001110011")));

    [Fact]
    public void Different_rules_are_a_change() =>
        Assert.True(PlanNotifier.HasChanged(Snapshot(0, "0011100"), Snapshot(0, "0011100", Rules with { OnToOffRatio = 1.2 })));

    [Fact]
    public void Losing_the_plan_is_a_change() =>
        Assert.True(PlanNotifier.HasChanged(Snapshot(0, "0011100"), Snapshot(1, "")));

    [Fact]
    public void Email_lists_plan_blocks_rules_and_tuning()
    {
        var tuning = new TuningResult(1.063, 630,
            [new RoomEstimate("AT3 Lobby", 4, 22.6, 0.14, 676, 3, 0.135, 1.063, null)]);

        var (subject, body) = PlanNotifier.Compose(
            Snapshot(0, "0011100"), Snapshot(0, "0011100", Rules with { OnToOffRatio = 0.25, MaxOffSlots = 12 }),
            ["new Agile prices until Fri 23:00", "tuning updated the rules"], tuning, Heating);

        Assert.Equal("Heating plan: on 1.5h of 3.5h, to Thu 20:00", subject);
        Assert.Contains("The heating plan has changed: new Agile prices until Fri 23:00; tuning updated the rules.", body);
        Assert.Contains("Now: off (16°C) for the slot starting Thu 16:00.", body);
        Assert.Contains("ON   Thu 17:30 – 19:00", body);
        Assert.Contains("off  Thu 16:30 – 17:30", body);
        Assert.Contains("Make-up ratio (on time per off time): 1  (was 0.25)", body);
        Assert.Contains("Maximum off: 600 min  (was 360 min)", body);
        Assert.Contains("Minimum on: 60 min", body);
        Assert.Contains("measured ratio 1.063, max off 630 min", body);
        Assert.Contains("AT3 Lobby: cooling 0.14°C/h (median of 4 falls, from 22.6°C)", body);
    }

    [Fact]
    public async Task Sends_once_per_change()
    {
        var email = new FakeEmail();
        var notifier = Create(email);

        Assert.True(await notifier.NotifyIfChangedAsync(Snapshot(0, "0011100"), [], null, CancellationToken.None));
        Assert.False(await notifier.NotifyIfChangedAsync(Snapshot(1, "011100"), [], null, CancellationToken.None));
        Assert.True(await notifier.NotifyIfChangedAsync(Snapshot(1, "001110"), [], null, CancellationToken.None));

        Assert.Equal(2, email.Sent.Count);
    }

    [Fact]
    public async Task Retries_after_a_failed_send()
    {
        var email = new FakeEmail { FailuresLeft = 1 };
        var notifier = Create(email);

        Assert.False(await notifier.NotifyIfChangedAsync(Snapshot(0, "0011100"), [], null, CancellationToken.None));
        Assert.True(await notifier.NotifyIfChangedAsync(Snapshot(1, "011100"), [], null, CancellationToken.None));

        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Sends_nothing_when_disabled()
    {
        var email = new FakeEmail();
        Assert.False(await Create(email, enabled: false).NotifyIfChangedAsync(Snapshot(0, "0011100"), [], null, CancellationToken.None));
        Assert.Empty(email.Sent);
    }
}

public class EmailOptionsTests
{
    [Fact]
    public void Requires_recipient_host_and_sender_only_when_enabled()
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(new object());
        Assert.Empty(new EmailOptions { Enabled = false }.Validate(context));

        var errors = new EmailOptions { Enabled = true, SmtpHost = "" }.Validate(context).Select(e => e.ErrorMessage).ToList();
        Assert.Equal(3, errors.Count);

        Assert.Empty(new EmailOptions { Enabled = true, To = ["me@example.com"], SmtpHost = "smtp.gmail.com", Username = "me@gmail.com" }.Validate(context));
    }
}
