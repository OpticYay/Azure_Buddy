using AzureBuddy.Core.Auth;
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
        return result.Success ? Ok(result.Tokens) : BadRequest(new { errors = result.Errors });
    }

    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return result.Success ? Ok(result.Tokens) : Unauthorized(new { errors = result.Errors });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken, cancellationToken);
        return result.Success ? Ok(result.Tokens) : Unauthorized(new { errors = result.Errors });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }
}
