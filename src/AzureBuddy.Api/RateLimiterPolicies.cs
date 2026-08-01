namespace AzureBuddy.Api;

/// <summary>Named rate-limiter policies, registered in Program.cs and referenced from controllers via
/// [EnableRateLimiting(...)]. Kept as named constants so the string can't drift between registration
/// and usage.</summary>
public static class RateLimiterPolicies
{
    /// <summary>Applied to register/login/refresh - throttles brute-force credential guessing and
    /// account-creation spam. See Program.cs for the actual limit (fixed window per client IP).</summary>
    public const string Auth = "auth";
}
