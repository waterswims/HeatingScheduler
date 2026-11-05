using HeatingScheduler.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace HeatingScheduler.Notifications;

public interface IEmailSender
{
    Task SendAsync(string subject, string body, CancellationToken cancellationToken);
}

/// <summary>Sends plain-text email over SMTP (e.g. Gmail with an app password) using <see cref="EmailOptions"/>.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.Sender));
        foreach (var to in o.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        var security = o.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(o.SmtpHost, o.SmtpPort, security, cancellationToken);
        if (!string.IsNullOrEmpty(o.Username))
        {
            await client.AuthenticateAsync(o.Username, o.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
