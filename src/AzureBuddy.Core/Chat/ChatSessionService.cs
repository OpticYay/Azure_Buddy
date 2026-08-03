using System.Text.Json;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Chat;

/// <summary>
/// Persists chat sessions/messages per user. Just like UserAdoConfigService, every query here filters
/// by the caller's userId - that's what makes "GET /api/chats/{sessionId}" return 404 rather than
/// someone else's conversation when a user guesses/increments an id (IDOR prevention). There is no
/// method that fetches a session by id alone.
/// </summary>
public sealed class ChatSessionService
{
    private const int MaxTitleLength = 60;
    private const string DefaultTitle = "New conversation";

    private readonly AppDbContext _dbContext;
    private readonly IAdoAttachmentService _adoAttachmentService;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly ILogger<ChatSessionService> _logger;

    public ChatSessionService(
        AppDbContext dbContext,
        IAdoAttachmentService adoAttachmentService,
        AdoConnectionContextAccessor connectionAccessor,
        ILogger<ChatSessionService> logger)
    {
        _dbContext = dbContext;
        _adoAttachmentService = adoAttachmentService;
        _connectionAccessor = connectionAccessor;
        _logger = logger;
    }

    public async Task<PagedResult<ChatSessionSummary>> ListSessionsAsync(
        string userId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // A session with zero messages isn't a conversation that happened - it's either the frontend's
        // "New Conversation" button used to create one eagerly on click, before the user had typed
        // anything (fixed - the frontend no longer does this, see ChatController.PostAsync's null
        // SessionId path), or the tail of some other create-then-append flow that got interrupted
        // between the two calls (e.g. a screenshot upload that fails right after its session was
        // created). Either way it has no content worth showing, so it's excluded here defensively -
        // this filter is what keeps that class of bug from ever being visible again, independent of
        // whether every path that CREATES a session is currently well-behaved.
        var query = _dbContext.ChatSessions
            .Where(s => s.UserId == userId && s.Messages.Any())
            .OrderByDescending(s => s.UpdatedAt);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new ChatSessionSummary(s.Id, s.Title, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ChatSessionSummary>(items, page, pageSize, totalCount);
    }

    public async Task<ChatSessionDetail?> GetSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        return session is null ? null : ToDetail(session);
    }

    public async Task<ChatSessionDetail> CreateSessionAsync(string userId, string? title, CancellationToken cancellationToken = default)
    {
        var session = new ChatSession
        {
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : Truncate(title)
        };

        _dbContext.ChatSessions.Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDetail(session);
    }

    /// <summary>Sets a session's title directly, overriding whatever the "first message becomes the
    /// title" default (see SaveNewMessageAsync) produced - the one thing that auto-titling can't do is
    /// know what the user would ACTUALLY call the conversation. A blank title falls back to
    /// DefaultTitle rather than saving an empty string, same as CreateSessionAsync's own handling of
    /// an unspecified title. Doesn't touch UpdatedAt - renaming is a metadata edit, not conversation
    /// activity, so it shouldn't reorder the session list the way a new message does.</summary>
    public async Task<ChatSessionSummary?> RenameSessionAsync(
        string userId, Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        var trimmed = title.Trim();
        session.Title = trimmed.Length == 0 ? DefaultTitle : Truncate(trimmed);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ChatSessionSummary(session.Id, session.Title, session.CreatedAt, session.UpdatedAt);
    }

    /// <summary>Removes every one of this user's sessions that has zero messages. Safe to delete
    /// unconditionally - a session with no messages represents no conversation that ever actually
    /// happened, the same judgment ListSessionsAsync's filter above makes. This is what actually clears
    /// out rows a client already created-then-abandoned (the old eager "New Conversation" click, or a
    /// screenshot upload that fails between creating its session and appending the message) rather than
    /// just hiding them from the list forever.</summary>
    public async Task<int> DeleteEmptySessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var empty = await _dbContext.ChatSessions
            .Where(s => s.UserId == userId && !s.Messages.Any())
            .ToListAsync(cancellationToken);

        if (empty.Count == 0)
        {
            return 0;
        }

        _dbContext.ChatSessions.RemoveRange(empty);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return empty.Count;
    }

    /// <summary>True if a session with this id exists and belongs to the caller (used by controllers
    /// to return 404 vs proceeding, without leaking whether the id exists for a *different* user).</summary>
    public async Task<bool> DeleteSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        // Cascade delete (configured in AppDbContext.OnModelCreating) removes all of this session's
        // messages in the same transaction.
        _dbContext.ChatSessions.Remove(session);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Appends a plain text message (no screenshot) - e.g. the assistant's reply, or a user
    /// message with no attachment. `type`/`tableHeaders`/`tableRows` let a caller that already knows
    /// the shape of what it's saving (see ChatController, which now gets this from IntentRouter's
    /// ChatReply) tag the message correctly instead of it always defaulting to plain Text.</summary>
    public async Task<ChatMessageView?> AppendMessageAsync(
        string userId,
        Guid sessionId,
        ChatMessageRole role,
        string content,
        int? workItemId,
        ChatMessageType type = ChatMessageType.Text,
        IReadOnlyList<string>? tableHeaders = null,
        IReadOnlyList<IReadOnlyList<string>>? tableRows = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        var message = new ChatMessage
        {
            SessionId = session.Id,
            Role = role,
            Content = content,
            WorkItemId = workItemId,
            Type = type,
            TableDataJson = tableHeaders is null ? null : JsonSerializer.Serialize(new ChatMessageTableData(tableHeaders, tableRows ?? Array.Empty<IReadOnlyList<string>>())),
        };

        await SaveNewMessageAsync(session, message, cancellationToken);
        return ToView(message);
    }

    /// <summary>
    /// The screenshot flow: delegate to IAdoAttachmentService to forward the image bytes straight to
    /// Azure DevOps and get back a URL, then store only that URL - never the bytes. This method never
    /// touches the raw ADO API shape itself (that's IAdoAttachmentService's job); its own job is purely
    /// "did the upload succeed, and what message do we record either way." Bytes only ever live in the
    /// `screenshotBytes` parameter for the duration of this call - nothing here writes them to disk, a
    /// field, or any longer-lived collection, and the parameter goes out of scope (eligible for GC) as
    /// soon as this method returns.
    ///
    /// If the ADO call fails, we still write a ChatMessage - but one whose content is the error and
    /// whose AdoAttachmentUrl stays null, so the chat history honestly reflects "this failed" instead
    /// of silently losing the user's screenshot with no record at all.
    /// </summary>
    public async Task<AppendMessageResult?> AppendMessageWithScreenshotAsync(
        string userId,
        Guid sessionId,
        string content,
        int workItemId,
        string fileName,
        byte[] screenshotBytes,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.ChatSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        var connection = _connectionAccessor.Require();

        try
        {
            var attachmentUrl = await _adoAttachmentService.AttachScreenshotAsync(
                connection, workItemId, fileName, screenshotBytes, cancellationToken);

            var message = new ChatMessage
            {
                SessionId = session.Id,
                Role = ChatMessageRole.User,
                Content = content,
                AdoAttachmentUrl = attachmentUrl,
                WorkItemId = workItemId
            };

            await SaveNewMessageAsync(session, message, cancellationToken);
            return new AppendMessageResult(true, ToView(message), null);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Screenshot attachment failed for session {SessionId}, work item {WorkItemId}.", sessionId, workItemId);

            var failureMessage = new ChatMessage
            {
                SessionId = session.Id,
                Role = ChatMessageRole.Assistant,
                Content = $"I couldn't attach that screenshot to work item #{workItemId} - Azure DevOps returned: {ex.Message}",
                AdoAttachmentUrl = null,
                WorkItemId = workItemId,
                Type = ChatMessageType.Error,
            };

            await SaveNewMessageAsync(session, failureMessage, cancellationToken);
            return new AppendMessageResult(false, ToView(failureMessage), ex.Message);
        }
    }

    private async Task SaveNewMessageAsync(ChatSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        _dbContext.ChatMessages.Add(message);
        session.UpdatedAt = DateTime.UtcNow;

        // First message in a session becomes its title (like most chat UIs), so the session list is
        // browsable without opening every conversation.
        if (session.Title == DefaultTitle && message.Role == ChatMessageRole.User)
        {
            session.Title = Truncate(message.Content);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxTitleLength ? text : text[..MaxTitleLength] + "…";

    private static ChatSessionDetail ToDetail(ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt, session.UpdatedAt, session.Messages.Select(ToView).ToList());

    private static ChatMessageView ToView(ChatMessage message) =>
        new(
            message.Id,
            message.Role,
            message.Content,
            message.AdoAttachmentUrl,
            message.WorkItemId,
            message.Type,
            message.TableDataJson is null ? null : JsonSerializer.Deserialize<ChatMessageTableData>(message.TableDataJson),
            message.CreatedAt);
}
