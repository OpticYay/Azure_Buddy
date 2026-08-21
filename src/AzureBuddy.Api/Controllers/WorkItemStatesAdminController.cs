using AzureBuddy.Core.Common;
using AzureBuddy.Core.WorkItemStates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// Admin-only CRUD over the per-work-item-type valid state list (see WorkItemStateConfiguration.cs
/// for why this is backend-configured rather than read live from Azure DevOps). Mirrors
/// LlmSettingsController's shape: [Authorize(Roles = "Admin")] is the actual gate, and this sits
/// under api/admin/... so the route itself signals "admin surface" to anyone reading the API.
/// </summary>
[ApiController]
[Route("api/admin/work-item-states")]
[Authorize(Roles = "Admin")]
public sealed class WorkItemStatesAdminController : ControllerBase
{
    private readonly WorkItemStateConfigService _service;

    public WorkItemStatesAdminController(WorkItemStateConfigService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkItemTypeStatesView>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var view = await _service.GetAllGroupedAsync(cancellationToken);
        return Ok(view);
    }

    [HttpPost]
    public async Task<ActionResult<WorkItemStateView>> CreateAsync(CreateWorkItemStateRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, cancellationToken);
        return Ok(created);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<WorkItemStateView>> UpdateAsync(int id, UpdateWorkItemStateRequest request, CancellationToken cancellationToken)
    {
        var updated = await _service.UpdateAsync(id, request, cancellationToken);
        return updated is null ? StateConfigNotFound() : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var deleted = await _service.DeleteAsync(id, cancellationToken);
        return deleted ? NoContent() : StateConfigNotFound();
    }

    /// <summary>Same ApiErrorResponse shape GlobalExceptionHandler uses for unhandled exceptions,
    /// instead of a bare empty-body NotFound() - see docs/improvements/04-refactor-and-dedup.md
    /// §4.11.</summary>
    private NotFoundObjectResult StateConfigNotFound() =>
        NotFound(new ApiErrorResponse(new ApiError("not_found", "Work item state configuration not found.")));
}
