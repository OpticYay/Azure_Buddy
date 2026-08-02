using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AzureBuddy.Core.Auth;

/// <summary>
/// Every user-scoped endpoint (settings, chat sessions) must resolve "which user is this?" from the
/// verified JWT claims attached to the request by the auth middleware - never from a userId in the
/// route/query/body, which a client could simply change to someone else's id (IDOR). This is the one
/// place that extraction happens, so every controller does it identically.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static string GetRequiredUserId(this ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            throw new InvalidOperationException("Request is authenticated but has no user id claim - this should be unreachable behind [Authorize].");
        }

        return userId;
    }
}
