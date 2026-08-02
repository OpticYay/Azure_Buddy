using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

/// <summary>
/// Chat history CRUD. Every action resolves the owning user from the JWT (User.GetRequiredUserId())
/// and ChatSessionService filters every query by that id - never trust sessionId alone as proof of
/// ownership (IDOR prevention, same pattern as SettingsController).
/// </summary>
[ApiController]
[Route("api/chats")]
[Authorize]
public sealed class ChatsController : ControllerBase
{
    private const long MaxScreenshotBytes = 10 * 1024 * 1024; // 10 MB

    private readonly ChatSessionService _chatSessionService;
    private readonly UserAdoConfigService _adoConfigService;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public ChatsController(
        ChatSessionService chatSessionService,
        UserAdoConfigService adoConfigService,
        AdoConnectionContextAccessor connectionAccessor)
    {
        _chatSessionService = chatSessionService;
        _adoConfigService = adoConfigService;
        _connectionAccessor = connectionAccessor;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<ChatSessionSummary>>> ListAsync(
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var result = await _chatSessionService.ListSessionsAsync(
            User.GetRequiredUserId(), page == 0 ? 1 : page, pageSize == 0 ? 20 : pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<ActionResult<ChatSessionDetail>> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _chatSessionService.GetSessionAsync(User.GetRequiredUserId(), sessionId, cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpPost]
    public async Task<ActionResult<ChatSessionDetail>> CreateAsync(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await _chatSessionService.CreateSessionAsync(User.GetRequiredUserId(), request.Title, cancellationToken);

        // Created(string, object) builds the Location header from a literal URI instead of going
        // through action-name link generation (CreatedAtAction) - the latter failed at runtime here
        // ("No route matches the supplied values"), caught by an integration test
        // (ChatsEndpointsTests.CreateSession_DefaultsTitleWhenNoneGiven) actually POSTing through the
        // real routing pipeline, which a controller-level unit test calling CreateAsync directly
        // wouldn't have exercised.
        return Created($"/api/chats/{session.Id}", session);
    }

    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var deleted = await _chatSessionService.DeleteSessionAsync(User.GetRequiredUserId(), sessionId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Accepts multipart/form-data so the same endpoint handles both a plain text message and one with
    /// an attached screenshot - "role"/"content"/"workItemId" as form fields, "screenshot" as an
    /// optional file part. If "screenshot" is present, workItemId is required (the attachment has to
    /// go somewhere in ADO) and the image bytes never touch this app's disk - see
    /// ChatSessionService.AppendMessageWithScreenshotAsync for where they're forwarded and discarded.
    ///
    /// "content" is intentionally NOT [Required] here (it used to be, and rejected a screenshot sent
    /// with no typed caption - a completely normal case - with a 400). It's only required when there's
    /// no screenshot, since a text message with no text doesn't mean anything; a screenshot with no
    /// caption defaults to a placeholder below instead of forcing the caller to invent filler text.
    /// </summary>
    [HttpPost("{sessionId:guid}/messages")]
    [RequestSizeLimit(MaxScreenshotBytes)]
    public async Task<IActionResult> AppendMessageAsync(
        Guid sessionId,
        [FromForm] ChatMessageRole role,
        [FromForm] string? content,
        [FromForm] int? workItemId,
        IFormFile? screenshot,
        CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();

        if (screenshot is null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return BadRequest(new ApiErrorResponse(new ApiError("validation_error", "The content field is required.", "content")));
            }

            var message = await _chatSessionService.AppendMessageAsync(userId, sessionId, role, content, workItemId, cancellationToken: cancellationToken);
            return message is null ? NotFound() : Ok(message);
        }

        if (workItemId is null)
        {
            return BadRequest(new ApiErrorResponse(new ApiError(
                "work_item_id_required",
                "workItemId is required when attaching a screenshot - the attachment must be linked to a specific ADO work item.",
                "workItemId")));
        }

        if (screenshot.Length == 0 || screenshot.Length > MaxScreenshotBytes)
        {
            return BadRequest(new ApiErrorResponse(new ApiError(
                "invalid_screenshot_size",
                $"Screenshot must be between 1 byte and {MaxScreenshotBytes / (1024 * 1024)} MB.",
                "screenshot")));
        }

        // Populate the per-request ADO connection before the service needs it - mirrors what
        // ChatController does for the live agent flow. Without saved ADO settings, this fails with a
        // clear AdoNotConfiguredException rather than a confusing downstream error.
        _connectionAccessor.Current = await _adoConfigService.GetConnectionContextAsync(userId, cancellationToken);

        // Read the upload into memory only for the duration of this request - nothing here writes it
        // to disk. The byte[] is passed straight to the ADO upload call and then goes out of scope.
        using var memoryStream = new MemoryStream();
        await screenshot.CopyToAsync(memoryStream, cancellationToken);
        var screenshotBytes = memoryStream.ToArray();

        // A screenshot with nothing typed alongside it is a normal case (evidence often speaks for
        // itself) - default to a placeholder instead of pushing that concern onto every caller.
        var effectiveContent = string.IsNullOrWhiteSpace(content) ? "Screenshot attached." : content;

        AppendMessageResult? result;
        try
        {
            result = await _chatSessionService.AppendMessageWithScreenshotAsync(
                userId, sessionId, effectiveContent, workItemId.Value, screenshot.FileName, screenshotBytes, cancellationToken);
        }
        catch (AdoNotConfiguredException ex)
        {
            return BadRequest(new ApiErrorResponse(new ApiError("ado_not_configured", ex.Message)));
        }

        if (result is null)
        {
            return NotFound();
        }

        // Still 200 even on a failed attachment - the failure is represented as a message in the chat
        // (per the "surface a clear error rather than silently dropping it" requirement), not an HTTP
        // error, since from the client's perspective the *request* succeeded (a message was recorded).
        return Ok(result);
    }
}
