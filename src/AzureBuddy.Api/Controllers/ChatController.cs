using System.ComponentModel.DataAnnotations;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Routing;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api.Controllers;

// [Required] targets the parameter, not "[property: ...]" - see AuthModels.cs's RegisterRequest
// comment for why that placement matters on a record's primary constructor.
public sealed record ChatRequest(Guid? SessionId, [Required] string Message);
public sealed record ChatResponse(Guid SessionId, string Reply);

/// <summary>
/// The live chat turn: one message in, one reply out. Now requires authentication (previously this
/// endpoint had no concept of a user at all). Two things changed to make that work:
///   1. The user's own ADO connection is resolved once per request and put on
///      AdoConnectionContextAccessor, which is how IntentRouter's deterministic flows and the
///      conversational agent's tools reach it - the ADO integration used to read a single global PAT
///      from config, that global config no longer exists (see AdoOptions/AdoClient).
///   2. Both the incoming user message and the resulting reply are persisted via ChatSessionService,
///      so this live flow produces the same durable history the /api/chats endpoints expose.
/// </summary>
[ApiController]
[Route("chat")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    private readonly IntentRouter _intentRouter;
    private readonly ChatSessionService _chatSessionService;
    private readonly UserAdoConfigService _adoConfigService;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public ChatController(
        IntentRouter intentRouter,
        ChatSessionService chatSessionService,
        UserAdoConfigService adoConfigService,
        AdoConnectionContextAccessor connectionAccessor)
    {
        _intentRouter = intentRouter;
        _chatSessionService = chatSessionService;
        _adoConfigService = adoConfigService;
        _connectionAccessor = connectionAccessor;
    }

    // [Required] on ChatRequest.Message (validated automatically by [ApiController]) covers both null
    // and whitespace-only values - RequiredAttribute trims strings before checking - so no manual
    // check is needed here anymore.
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> PostAsync([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();

        // Populate the per-request ADO connection *before* anything that might call Azure DevOps runs.
        // Left null if the user hasn't saved ADO settings yet - flows that actually need it will raise
        // AdoNotConfiguredException, caught below.
        _connectionAccessor.Current = await _adoConfigService.GetConnectionContextAsync(userId, cancellationToken);

        var session = request.SessionId is { } existingId
            ? await _chatSessionService.GetSessionAsync(userId, existingId, cancellationToken)
            : null;

        if (request.SessionId is not null && session is null)
        {
            // Either a bad id or (deliberately, to avoid leaking existence) someone else's session id.
            return NotFound("Chat session not found.");
        }

        session ??= await _chatSessionService.CreateSessionAsync(userId, title: null, cancellationToken);

        await _chatSessionService.AppendMessageAsync(userId, session.Id, ChatMessageRole.User, request.Message, workItemId: null, cancellationToken);

        string reply;
        try
        {
            // IntentRouter's own in-memory buffer (for LLM context) is keyed by this same session id,
            // so short-term conversational memory and durable history line up 1:1.
            reply = await _intentRouter.RouteAsync(session.Id.ToString(), request.Message, cancellationToken);
        }
        catch (AdoNotConfiguredException ex)
        {
            reply = ex.Message;
        }

        await _chatSessionService.AppendMessageAsync(userId, session.Id, ChatMessageRole.Assistant, reply, workItemId: null, cancellationToken);

        return Ok(new ChatResponse(session.Id, reply));
    }
}
