namespace AzureBuddy.Api;

/// <summary>Named rate-limiter policies, registered in Program.cs and referenced from controllers via
/// [EnableRateLimiting(...)]. Kept as named constants so the string can't drift between registration
/// and usage.</summary>
public static class RateLimiterPolicies
{
    /// <summary>Applied to register/login/refresh - throttles brute-force credential guessing and
    /// account-creation spam. See Program.cs for the actual limit (fixed window per client IP).</summary>
    public const string Auth = "auth";

    /// <summary>Applied to ChatController's live chat endpoint - every call makes at least one billed
    /// LLM request and potentially several Azure DevOps API calls, so this is both a cost-control and
    /// abuse-protection gap if left unprotected. Partitioned per authenticated user (not per IP, unlike
    /// Auth above) since the caller is already authenticated by the time this endpoint is reached - see
    /// Program.cs for the actual limit.</summary>
    public const string Chat = "chat";
}
