using AzureBuddy.Core.Chat;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using LlmChatRole = AzureBuddy.Core.Llm.Models.ChatRole;

namespace AzureBuddy.Tests.Chat;

public class DbChatHistorySourceTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ChatSession NewSession(AppDbContext dbContext, string userId = "user-1")
    {
        var session = new ChatSession { UserId = userId, Title = "conversation" };
        dbContext.ChatSessions.Add(session);
        return session;
    }

    [Fact]
    public async Task LoadRecentAsync_ExcludesErrorTypedMessages()
    {
        using var dbContext = NewDbContext();
        var session = NewSession(dbContext);
        var baseTime = DateTime.UtcNow;

        dbContext.ChatMessages.AddRange(
            new ChatMessage { SessionId = session.Id, Role = ChatMessageRole.User, Content = "file a bug", CreatedAt = baseTime },
            new ChatMessage
            {
                SessionId = session.Id,
                Role = ChatMessageRole.Assistant,
                Content = "Sorry, I couldn't get a response from the AI model just now.",
                Type = ChatMessageType.Error,
                CreatedAt = baseTime.AddSeconds(1),
            });
        await dbContext.SaveChangesAsync();

        var source = new DbChatHistorySource(dbContext);
        var messages = await source.LoadRecentAsync(session.Id.ToString(), maxMessages: 15);

        Assert.Single(messages);
        Assert.Equal("file a bug", messages[0].Content);
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsOldestFirst_MappingRolesCorrectly()
    {
        using var dbContext = NewDbContext();
        var session = NewSession(dbContext);
        var baseTime = DateTime.UtcNow;

        dbContext.ChatMessages.AddRange(
            new ChatMessage { SessionId = session.Id, Role = ChatMessageRole.User, Content = "first", CreatedAt = baseTime },
            new ChatMessage { SessionId = session.Id, Role = ChatMessageRole.Assistant, Content = "second", CreatedAt = baseTime.AddSeconds(1) });
        await dbContext.SaveChangesAsync();

        var source = new DbChatHistorySource(dbContext);
        var messages = await source.LoadRecentAsync(session.Id.ToString(), maxMessages: 15);

        Assert.Equal(2, messages.Count);
        Assert.Equal("first", messages[0].Content);
        Assert.Equal(LlmChatRole.User, messages[0].Role);
        Assert.Equal("second", messages[1].Content);
        Assert.Equal(LlmChatRole.Assistant, messages[1].Role);
    }

    [Fact]
    public async Task LoadRecentAsync_MoreRowsThanMaxMessages_ReturnsOnlyTheMostRecent()
    {
        using var dbContext = NewDbContext();
        var session = NewSession(dbContext);
        var baseTime = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            dbContext.ChatMessages.Add(new ChatMessage
            {
                SessionId = session.Id,
                Role = ChatMessageRole.User,
                Content = $"message-{i}",
                CreatedAt = baseTime.AddSeconds(i),
            });
        }
        await dbContext.SaveChangesAsync();

        var source = new DbChatHistorySource(dbContext);
        var messages = await source.LoadRecentAsync(session.Id.ToString(), maxMessages: 2);

        Assert.Equal(2, messages.Count);
        Assert.Equal("message-3", messages[0].Content);
        Assert.Equal("message-4", messages[1].Content);
    }

    [Fact]
    public async Task LoadRecentAsync_InvalidSessionId_ReturnsEmpty()
    {
        using var dbContext = NewDbContext();
        var source = new DbChatHistorySource(dbContext);

        var messages = await source.LoadRecentAsync("not-a-guid", maxMessages: 15);

        Assert.Empty(messages);
    }
}
