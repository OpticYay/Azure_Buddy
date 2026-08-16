using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Seam between a chat history store and MySQL, kept separate from the store itself so
/// InMemoryChatHistoryStore/RedisChatHistoryStore never need to reference AppDbContext directly -
/// matching the repo's existing IAdoClient/IEmailSender pattern for isolating an external dependency
/// behind an interface. Implemented by DbChatHistorySource (AzureBuddy.Core.Chat).</summary>
public interface IChatHistorySource
{
    /// <summary>The most recent `maxMessages` messages for a session, oldest first, ready to seed a
    /// fresh ChatSessionWindow after a cache miss.</summary>
    Task<IReadOnlyList<ChatMessage>> LoadRecentAsync(string sessionId, int maxMessages, CancellationToken cancellationToken = default);
}
