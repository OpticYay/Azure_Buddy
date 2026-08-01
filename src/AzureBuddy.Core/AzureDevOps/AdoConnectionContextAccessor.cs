namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Holds "which user's ADO connection applies to this request", set once per HTTP request (registered
/// Scoped in DI, so one instance per request) by the controller after resolving the caller's identity
/// from their JWT. Everything deeper in the call chain - IntentRouter, the deterministic flows, the
/// agent's tool functions - reads it from here instead of every method signature needing an extra
/// "AdoConnectionContext context" parameter threaded all the way down. IAdoClient itself still takes
/// the context explicitly as a parameter (kept stateless/testable) - this accessor is just how the
/// callers *above* IAdoClient get hold of the value to pass in.
/// </summary>
public sealed class AdoConnectionContextAccessor
{
    public AdoConnectionContext? Current { get; set; }

    /// <summary>Throws a user-facing error if called before the context was set - this should only
    /// happen if a user tries to use ADO features before saving their ADO settings.</summary>
    public AdoConnectionContext Require()
    {
        return Current ?? throw new AdoNotConfiguredException(
            "No Azure DevOps connection is configured for this account. Add your organization, project, and Personal Access Token under Settings first.");
    }
}

/// <summary>Thrown when a request needs ADO access but the current user hasn't saved ADO settings yet.
/// Callers (IntentRouter, controllers) should catch this and return a clear message rather than a 500.</summary>
public sealed class AdoNotConfiguredException : Exception
{
    public AdoNotConfiguredException(string message) : base(message)
    {
    }
}
