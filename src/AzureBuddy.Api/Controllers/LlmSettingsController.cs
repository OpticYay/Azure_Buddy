using AzureBuddy.Core.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// Admin-only: the app-wide LLM provider configuration (which provider is primary, optional
/// fallback, models, timeouts, and the Gemini API key). [Authorize(Roles = "Admin")] here is what
/// actually gates this - see Program.cs's admin-role-seeding block for how a user becomes an Admin,
/// and TokenService.CreateAccessToken for where the "role" claim this attribute checks comes from.
///
/// Deliberately under api/admin/... rather than api/settings/llm alongside SettingsController's ADO
/// endpoints - the route itself signals "this is an admin surface" to anyone reading the API, not
/// just the attribute.
/// </summary>
[ApiController]
[Route("api/admin/llm")]
[Authorize(Roles = "Admin")]
public sealed class LlmSettingsController : ControllerBase
{
    private readonly LlmSettingsService _llmSettingsService;

    public LlmSettingsController(LlmSettingsService llmSettingsService)
    {
        _llmSettingsService = llmSettingsService;
    }

    [HttpGet]
    public async Task<ActionResult<LlmSettingsView>> GetAsync(CancellationToken cancellationToken)
    {
        var view = await _llmSettingsService.GetAsync(cancellationToken);
        return Ok(view);
    }

    // [ApiController]'s automatic model validation (via SaveLlmSettingsRequest's [Required]/[Url]/
    // [Range] attributes) rejects a malformed request with the app's standard ApiErrorResponse shape
    // before this action body even runs - see Program.cs's InvalidModelStateResponseFactory.
    [HttpPut]
    public async Task<ActionResult<LlmSettingsView>> PutAsync(SaveLlmSettingsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var view = await _llmSettingsService.SaveAsync(request, cancellationToken);
            return Ok(view);
        }
        catch (ArgumentException ex)
        {
            // Thrown by LlmSettingsService when PrimaryProvider isn't a recognized provider name -
            // the one piece of validation [Required]/[Url]/[Range] attributes can't express, since
            // "must be one of these specific strings" needs actual logic, not a declarative attribute.
            return BadRequest(new AzureBuddy.Core.Common.ApiErrorResponse(
                new AzureBuddy.Core.Common.ApiError("invalid_provider", ex.Message, "primaryProvider")));
        }
    }
}
