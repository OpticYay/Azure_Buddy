using AzureBuddy.Core.Account;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// [Authorize] (inherited from the global default policy too, but explicit for clarity): every action
/// requires a valid JWT, and the user id used everywhere below comes from User.GetRequiredUserId() -
/// the verified token's own claims - never from a route/query parameter, which is what stops one user
/// from reading or changing another user's account (IDOR). Every regular logged-in user can reach
/// every action here for their own account; there's no role check.
/// </summary>
[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountController : ControllerBase
{
    private readonly AccountService _accountService;

    public AccountController(AccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet("profile")]
    public async Task<ActionResult<ProfileView>> GetProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _accountService.GetProfileAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(profile);
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _accountService.UpdateProfileAsync(User.GetRequiredUserId(), request, cancellationToken);
        if (!result.Success)
        {
            // A duplicate email is a genuine conflict with another resource (409); anything else
            // (a policy failure from Identity's own validator slipping past our pre-check, e.g. a race)
            // is a regular 400.
            var isDuplicate = result.Errors.Any(e => e.Code == "duplicate_email" || e.Code == "DuplicateEmail");
            var errorResponse = new ApiErrorResponse(result.Errors);
            return isDuplicate ? Conflict(errorResponse) : BadRequest(errorResponse);
        }

        return Ok(new UpdateProfileResponse(result.Profile!, result.RequiresReLogin));
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _accountService.ChangePasswordAsync(User.GetRequiredUserId(), request, cancellationToken);
        return result.Success ? NoContent() : BadRequest(new ApiErrorResponse(result.Errors));
    }
}

/// <summary>Wraps UpdateProfileResult's two useful-to-the-client fields for the response body - the
/// frontend reads requiresReLogin to decide whether to redirect to /login (see UpdateProfileResult's
/// docs for why an email change needs that).</summary>
public sealed record UpdateProfileResponse(ProfileView Profile, bool RequiresReLogin);
