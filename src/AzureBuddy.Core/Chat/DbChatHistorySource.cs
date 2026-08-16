using AzureBuddy.Core.Agent;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using LlmChatMessage = AzureBuddy.Core.Llm.Models.ChatMessage;

namespace AzureBuddy.Core.Chat;

/// <summary>
/// Rehydrates a chat window from MySQL on a cache miss. Deliberately its own class rather than a method
/// on ChatSessionService: ChatSessionService already depends on IChatHistoryStore's consumers indirectly
/// (it will call EvictAsync on delete), and implementing IChatHistorySource there too would create a
/// runtime DI cycle (ChatSessionService -> IChatHistoryStore -> IChatHistorySource -> ChatSessionService).
/// Scoped, same lifetime as the AppDbContext it wraps.
/// </summary>
public sealed class DbChatHistorySource : IChatHistorySource
{
    private readonly AppDbContext _dbContext;

    public DbChatHistorySource(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<LlmChatMessage>> LoadRecentAsync(
        string sessionId, int maxMessages, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out var guid))
        {
            return Array.Empty<LlmChatMessage>();
        }

        // Type != Error is deliberate: replaying "Sorry, I couldn't get a response from the AI model"
        // back to the model as prior context teaches it to apologize. Table-typed messages rehydrate
        // fine as plain assistant text - IntentRouter already bakes the rendered markdown table into
        // ChatReply.Text before it's ever persisted, so nothing table-specific needs to survive here.
        var rows = await _dbContext.ChatMessages
            .Where(m => m.SessionId == guid && m.Type != ChatMessageType.Error)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(cancellationToken);

        rows.Reverse();

        return rows
            .Select(row => row.Role == ChatMessageRole.User
                ? LlmChatMessage.User(row.Content)
                : LlmChatMessage.Assistant(row.Content))
            .ToList();
    }
}
