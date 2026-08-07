using AzureBuddy.Core.Auth;

namespace AzureBuddy.Tests.Integration;

/// <summary>Test double for IEmailSender - captures every "sent" email (subject/body, including the
/// reset/confirmation link) so tests can extract the token out of the body instead of needing a real
/// mailbox, and never actually touches a network.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public sealed record SentEmail(string ToEmail, string Subject, string Body);

    public List<SentEmail> SentEmails { get; } = new();

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        SentEmails.Add(new SentEmail(toEmail, subject, body));
        return Task.CompletedTask;
    }

    public void Reset() => SentEmails.Clear();
}
