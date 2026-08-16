using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBuddy.Core.Auth;

/// <summary>
/// Plain SMTP sender (System.Net.Mail.SmtpClient) - the most portable option since it works with any
/// SMTP-speaking provider (a real mail server, SendGrid/SES/Mailgun's SMTP relay, etc.) without a
/// vendor-specific SDK dependency. Only registered when Email:Smtp:Host is actually configured - see
/// AuthServiceCollectionExtensions.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
        };

        if (!string.IsNullOrEmpty(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        var fromAddress = string.IsNullOrEmpty(_options.FromAddress) ? _options.Username : _options.FromAddress;
        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, _options.FromDisplayName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        message.To.Add(toEmail);

        try
        {
            // SmtpClient's async methods don't accept a CancellationToken (a long-standing gap in the
            // .NET BCL) - SendMailAsync is used as-is rather than wrapped in a manual cancellation race,
            // which would abandon the send without actually stopping it and risk sending twice on retry.
            await client.SendMailAsync(message);
        }
        catch (SmtpException ex)
        {
            // A failed send should never take down the calling flow (registration, forgot-password) -
            // AuthService already treats email sending as best-effort and logs this itself, but logging
            // here too captures the SMTP-specific detail (status code) that AuthService's catch can't see.
            _logger.LogWarning(ex, "SMTP send to {ToEmail} failed: {StatusCode}", toEmail, ex.StatusCode);
            throw;
        }
    }
}
