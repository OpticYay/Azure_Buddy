using AzureBuddy.Core.Common;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Auth;

/// <summary>
/// Lets AuthService send password-reset and email-confirmation mail without hardcoding a specific
/// provider - SmtpEmailSender is a real implementation (see AuthServiceCollectionExtensions for when
/// it's registered vs. the no-op fallback below), and a future SendGrid/SES-backed implementation can
/// be dropped in the same way, without touching AuthService or AuthController.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Registered whenever no SMTP host is configured (see AuthServiceCollectionExtensions) -
/// logs instead of sending, so registration/password-reset flows that depend on IEmailSender don't
/// crash or silently no-op with zero trace: an admin or developer running without SMTP configured can
/// still read the email that *would* have gone out (including the reset/confirmation link) straight
/// from the application log.</summary>
public sealed class NoOpEmailSender : IEmailSender
{
    private readonly ILogger<NoOpEmailSender> _logger;

    public NoOpEmailSender(ILogger<NoOpEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        // The body carries password-reset/email-confirmation links and tokens - logging it at
        // Information would let anyone with log read access complete either flow for any user, and
        // this is the DEFAULT path whenever Email:Smtp:Host is blank (every local/dev environment by
        // default), not a rare fallback. Debug is opt-in and expected to be off in any shared/persisted
        // log sink - this must never be raised back to Information.
        _logger.LogInformation(
            "No email provider configured (see Email:Smtp:Host) - would have sent to {MaskedEmail}, subject {Subject}.",
            EmailMasking.Mask(toEmail), subject);
        // Deliberately logs the FULL, unmasked body here (unlike every other line in this class) -
        // recovering the actual reset/confirmation link is the entire point of this fallback (see the
        // class doc comment), and a redacted link would make it useless for that. The address itself
        // doesn't need to stay unmasked for that purpose, so it's masked the same as everywhere else.
        // The Debug level is the guardrail on the body: it's off by default and expected to stay off in
        // any shared/persisted log sink, so this line only ever surfaces in a local console a developer
        // is deliberately watching.
        _logger.LogDebug("Email body that would have been sent to {MaskedEmail}:\n{Body}", EmailMasking.Mask(toEmail), body);
        return Task.CompletedTask;
    }
}
