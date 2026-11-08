using HeatingScheduler.Notifications;
using HeatingScheduler.Options;
using HeatingScheduler.Scheduling;
using HeatingScheduler.Tuning;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeatingScheduler.Tests;

public class PlanNotifierTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-05T16:00:00Z");
    private static readonly HeatingOptions Heating = new() { OnTemperature = 22.5, OffTemperature = 16 };
    private static readonly RoomModel Lobby = new("d/3", "AT3 Lobby", 0.137, 0.168, 21.8, T0);

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

    /// <summary>A snapshot whose current slot is T0 + <paramref name="offset"/> slots, with upcoming states ('1' = on).</summary>
    private static PlanSnapshot Snapshot(int offset, string states, RoomModel? room = null)
    {
        var current = T0 + offset * Slots.Length;
        var upcoming = states.Select((c, i) => new PlannedSlot(
            current + (i + 1) * Slots.Length, c == '1' ? HeatingState.On : HeatingState.Off, 20m + i,
            new Dictionary<string, double> { ["d/3"] = 21.5 - 0.05 * i }, 21.45 - 0.05 * i)).ToList();
        return new PlanSnapshot(current, new DesiredState(HeatingState.Off, false), 19m, upcoming, [room ?? Lobby], 20.7);
    }

    private static CommittedSlot Happened(int offset, bool on, decimal price, double temp) =>
        new(T0 + offset * Slots.Length, new DesiredState(on ? HeatingState.On : HeatingState.Off, false), price,
            new Dictionary<string, double> { ["d/3"] = temp });

    [Fact]
    public void Email_has_previous_plan_what_happened_and_new_plan()
    {
        var previous = Snapshot(0, "0011", Lobby with { CoolingPerHour = 0.15, HeatingPerHour = 0.15 });
        var happened = new[] { Happened(0, false, 19m, 21.6), Happened(1, false, 20m, 21.5), Happened(2, true, 21m, 21.4), Happened(3, true, 22m, 21.5) };
        var current = Snapshot(4, "011");
        var tuning = new TuningResult([new RoomEstimate("d/3", "AT3 Lobby", 7, 0.137, 4, 0.168, null)]);

        var (subject, body) = PlanNotifier.Compose(previous, happened, current, ["new Agile prices until Fri 23:00", "room rates re-measured"], tuning, Heating);

        Assert.Equal("Heating plan: on 1h of 1.5h, to Thu 20:00", subject);
        Assert.StartsWith("New Agile prices until Fri 23:00; room rates re-measured.", body);

        Assert.Contains("PLAN IN THE LAST EMAIL (sent during the slot starting Thu 16:00)", body);
        Assert.Contains("off  Thu 16:00 – 17:30        1.5h   avg 20.00p/kWh   → 21.4°C", body);   // includes the slot current then
        Assert.Contains("ON   Thu 17:30 – 18:30          1h   avg 22.50p/kWh   → 21.3°C", body);

        Assert.Contains("WHAT HAPPENED (Thu 16:00 – 18:00)", body);
        Assert.Contains("off  Thu 16:00 – 17:00          1h   avg 19.50p/kWh   21.6°C → 21.4°C", body);
        Assert.Contains("ON   Thu 17:00 – 18:00          1h   avg 21.50p/kWh   21.4°C → 21.8°C", body);
        Assert.Contains("On for 1h at an average of 21.50p. The last email planned 0.5h at an average of 22.00p for the same period.", body);

        Assert.Contains("NEW PLAN", body);
        Assert.Contains("Now: off (16°C) for the slot starting Thu 18:00.", body);
        Assert.Contains("ON   Thu 19:00 – 20:00", body);
        Assert.Contains("Lowest predicted temperature:", body);
        Assert.Contains("AT3 Lobby: now 21.8°C, cooling 0.137°C/h (was 0.150), heating 0.168°C/h (was 0.150)", body);
        Assert.Contains("AT3 Lobby: cooling 0.137°C/h (median of 7 falls), heating 0.168°C/h (median of 4 rises)", body);
    }

    [Fact]
    public void End_times_show_the_day_when_it_changes()
    {
        var previous = Snapshot(14, "000000001111");      // Thu 23:00 onwards
        var (_, body) = PlanNotifier.Compose(previous, [], Snapshot(20, "0"), [], null, Heating);

        Assert.Contains("off  Thu 23:00 – Fri 03:30", body);
        Assert.Contains("WHAT HAPPENED (Thu 23:00 – Fri 02:00)", body);
    }

    [Fact]
    public void First_email_says_there_is_nothing_to_compare_with()
    {
        var (_, body) = PlanNotifier.Compose(null, [], Snapshot(0, "0011"), [], null, Heating);

        Assert.Contains("None: this is the first email since the service started.", body);
        Assert.Contains("Nothing to compare with yet.", body);
    }

    [Fact]
    public void Fallback_slots_are_marked()
    {
        var happened = new[] { new CommittedSlot(T0, new DesiredState(HeatingState.On, true), null, new Dictionary<string, double>()) };
        var (_, body) = PlanNotifier.Compose(Snapshot(0, "0"), happened, Snapshot(1, "0"), [], null, Heating);

        Assert.Contains("(fallback: no plan)", body);
    }

    private static (PlanNotifier Notifier, HeatingSchedule Schedule) Create(FakeEmail email, bool enabled = true)
    {
        var schedule = new HeatingSchedule(
            TestHelpers.Opt(new ScheduleOptions()), TestHelpers.Opt(Heating), NullLogger<HeatingSchedule>.Instance);
        schedule.UpdateTemperatures([new RoomTemperature("d/3", "AT3 Lobby", 21.5, T0)]);
        schedule.UpdatePrices(TestHelpers.SlotsFrom(T0, 8, i => 10m + i), T0);
        var notifier = new PlanNotifier(
            email, schedule,
            TestHelpers.Opt(new EmailOptions { Enabled = enabled, To = ["me@example.com"], SmtpHost = "smtp.example.com", From = "app@example.com" }),
            TestHelpers.Opt(Heating), NullLogger<PlanNotifier>.Instance);
        return (notifier, schedule);
    }

    [Fact]
    public async Task Sends_every_time_it_is_asked()
    {
        var email = new FakeEmail();
        var (notifier, _) = Create(email);

        Assert.True(await notifier.NotifyAsync([], null, CancellationToken.None));
        Assert.True(await notifier.NotifyAsync([], null, CancellationToken.None));

        Assert.Equal(2, email.Sent.Count);
        Assert.Contains("WHAT HAPPENED", email.Sent[1].Body);
    }

    [Fact]
    public async Task Reports_a_failed_send_so_it_can_be_retried()
    {
        var email = new FakeEmail { FailuresLeft = 1 };
        var (notifier, _) = Create(email);

        Assert.False(await notifier.NotifyAsync([], null, CancellationToken.None));
        Assert.True(await notifier.NotifyAsync([], null, CancellationToken.None));
        Assert.Contains("first email since the service started", Assert.Single(email.Sent).Body);
    }

    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        var email = new FakeEmail();
        Assert.True(await Create(email, enabled: false).Notifier.NotifyAsync([], null, CancellationToken.None));
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
