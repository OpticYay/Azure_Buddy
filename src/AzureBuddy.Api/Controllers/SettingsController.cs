using AzureBuddy.Core.Auth;
using AzureBuddy.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// [Authorize] here (inherited from the global default policy too, but explicit for clarity) means
/// every action requires a valid JWT. The user id used for every query below comes from
/// User.GetRequiredUserId() - i.e. the verified token's claims - never from a route/query parameter,
/// which is what stops one user from reading or overwriting another user's ADO settings (IDOR).
/// </summary>
[ApiController]
[Route("api/settings/ado")]
[Authorize]
public sealed class SettingsController : ControllerBase
{
    private readonly UserAdoConfigService _adoConfigService;

    public SettingsController(UserAdoConfigService adoConfigService)
    {
        _adoConfigService = adoConfigService;
    }

    [HttpGet]
    public async Task<ActionResult<AdoSettingsView>> GetAsync(CancellationToken cancellationToken)
    {
        var view = await _adoConfigService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(view);
    }

    // [ApiController]'s automatic model validation now rejects a missing/malformed request (see the
    // [Required]/[Url] attributes on SaveAdoSettingsRequest) before this action body even runs - no
    // manual null/empty checks needed here anymore.
    [HttpPut]
    public async Task<ActionResult<AdoSettingsView>> PutAsync(SaveAdoSettingsRequest request, CancellationToken cancellationToken)
    {
        var view = await _adoConfigService.UpsertAsync(User.GetRequiredUserId(), request, cancellationToken);
        return Ok(view);
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAsync(CancellationToken cancellationToken)
    {
        await _adoConfigService.DeleteAsync(User.GetRequiredUserId(), cancellationToken);
        return NoContent();
    }

    [HttpPost("test-connection")]
    public async Task<ActionResult<TestConnectionResult>> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var result = await _adoConfigService.TestConnectionAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(result);
    }
}
