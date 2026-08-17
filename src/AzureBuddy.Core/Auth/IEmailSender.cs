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
            "No email provider configured (see Email:Smtp:Host) - would have sent to {ToEmail}, subject {Subject}.",
            toEmail, subject);
        _logger.LogDebug("Email body that would have been sent to {ToEmail}:\n{Body}", toEmail, body);
        return Task.CompletedTask;
    }
}
