using System.ComponentModel.DataAnnotations;

namespace HeatingScheduler.Options;

/// <summary>Email notifications sent whenever the heating plan changes.</summary>
public sealed class EmailOptions : IValidatableObject
{
    public const string SectionName = "Email";

    /// <summary>When false, no emails are sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Addresses that receive the notifications.</summary>
    public List<string> To { get; set; } = [];

    /// <summary>Sender address. Defaults to <see cref="Username"/>.</summary>
    public string? From { get; set; }

    /// <summary>SMTP server, e.g. smtp.gmail.com.</summary>
    public string SmtpHost { get; set; } = "";

    /// <summary>SMTP port: 587 (STARTTLS) or 465 (TLS). Outbound port 25 is blocked on EC2.</summary>
    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    public string Username { get; set; } = "";

    /// <summary>SMTP password. For Gmail, an app password.</summary>
    public string Password { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (To.Count == 0 || To.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult("Email:To needs at least one address when email is enabled.", [nameof(To)]);
        }

        if (string.IsNullOrWhiteSpace(SmtpHost))
        {
            yield return new ValidationResult("Email:SmtpHost is required when email is enabled.", [nameof(SmtpHost)]);
        }

        if (string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(Username))
        {
            yield return new ValidationResult("Email:From or Email:Username is required when email is enabled.", [nameof(From)]);
        }
    }

    public string Sender => string.IsNullOrWhiteSpace(From) ? Username : From;
}
