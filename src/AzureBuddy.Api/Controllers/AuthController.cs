using AzureBuddy.Core.Auth;
using AzureBuddy.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// [AllowAnonymous] on every action here because these are the only endpoints that don't have a JWT
/// yet - that's the whole point of them. Every other controller in the app defaults to requiring auth
/// (see Program.cs's global [Authorize] fallback policy).
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimiterPolicies.Auth)]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        return result.Success ? Ok(result.Tokens) : BadRequest(new ApiErrorResponse(result.Errors));
    }

    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return result.Success ? Ok(result.Tokens) : Unauthorized(new ApiErrorResponse(result.Errors));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken, cancellationToken);
        return result.Success ? Ok(result.Tokens) : Unauthorized(new ApiErrorResponse(result.Errors));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    // Always 204, whether or not the email has an account - see AuthService.ForgotPasswordAsync's
    // docs for why a differing response here would let an attacker enumerate registered emails.
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await _authService.ForgotPasswordAsync(request.Email, cancellationToken);
        return NoContent();
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.ResetPasswordAsync(request, cancellationToken);
        return result.Success ? Ok(result.Tokens) : BadRequest(new ApiErrorResponse(result.Errors));
    }

    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.ConfirmEmailAsync(request, cancellationToken);
        return result.Success ? Ok(result.Tokens) : BadRequest(new ApiErrorResponse(result.Errors));
    }

    // Always 204, same non-enumeration reasoning as forgot-password.
    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmationAsync(ResendConfirmationRequest request, CancellationToken cancellationToken)
    {
        await _authService.ResendConfirmationEmailAsync(request.Email, cancellationToken);
        return NoContent();
    }
}
