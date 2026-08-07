using AzureBuddy.Core.WorkItemStates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// Read-only, any-authenticated-user lookup of the enabled/ordered state list for one work item type -
/// used by the chat flow (UpdateItemFlow, AdoWorkItemToolset) and by the frontend to know what states
/// are valid before offering them, without needing Admin rights just to read the list Admin manages.
/// </summary>
[ApiController]
[Route("api/work-item-states")]
[Authorize]
public sealed class WorkItemStatesController : ControllerBase
{
    private readonly WorkItemStateConfigService _service;

    public WorkItemStatesController(WorkItemStateConfigService service)
    {
        _service = service;
    }

    [HttpGet("{workItemType}")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetAsync(string workItemType, CancellationToken cancellationToken)
    {
        var states = await _service.GetEnabledStateNamesAsync(workItemType, cancellationToken);
        return Ok(states);
    }
}
