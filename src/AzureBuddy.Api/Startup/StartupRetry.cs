namespace AzureBuddy.Api.Startup;

/// <summary>Retries a startup action a handful of times with a short linear backoff before giving up -
/// enough to survive the normal container-orchestration race where the app starts before its MySQL
/// dependency is accepting connections, not a substitute for a sustained-outage retry/circuit-breaker
/// strategy. Used for the two pre-app.Run() scopes (admin role seeding, LLM settings load) that used to
/// throw straight out of Program.cs's top-level statements and kill the process before app.Run() ever
/// started - which meant even /health/live never came up to report what had gone wrong.</summary>
public static class StartupRetry
{
    public static async Task RunAsync(
        string operationName,
        ILogger logger,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        int maxAttempts = 5,
        TimeSpan? delayBetweenAttempts = null)
    {
        var delay = delayBetweenAttempts ?? TimeSpan.FromSeconds(3);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(ex,
                    "Startup step '{Operation}' failed on attempt {Attempt}/{MaxAttempts} - retrying in {Delay}.",
                    operationName, attempt, maxAttempts, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }

        // Final attempt: let a failure here throw for real. Retrying is meant to ride out the brief
        // "MySQL isn't up yet" race, not to mask a genuinely broken deployment - after maxAttempts, the
        // process should still fail loudly and exit rather than silently run half-initialized.
        await action(cancellationToken);
    }
}
