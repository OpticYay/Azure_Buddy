namespace AzureBuddy.Core.Auth;

/// <summary>
/// Extension point for a future feature, not implemented in this pass: email verification and
/// password-reset-via-email both need a way to actually send mail. Rather than hardcode "no email
/// support" into AuthController, this interface lets a real implementation (SendGrid, SMTP, etc.) be
/// dropped in later without touching the controller - just register a different implementation in DI.
/// NoOpEmailSender below is the only implementation registered today; it deliberately does nothing.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Registered by default. Logs instead of sending, so registration/password-reset flows that
/// depend on IEmailSender don't crash - they just won't actually deliver mail until a real sender
/// (e.g. SendGrid, SMTP) is wired in.</summary>
public sealed class NoOpEmailSender : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
