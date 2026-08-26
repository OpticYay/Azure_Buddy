using AzureBuddy.Core.Chat;
using Xunit;

namespace AzureBuddy.Tests.Chat;

public class ChatSessionContextAccessorTests
{
    [Fact]
    public void SessionId_DefaultsToNull()
    {
        var accessor = new ChatSessionContextAccessor();

        Assert.Null(accessor.SessionId);
    }

    [Fact]
    public void SessionId_CanBeSetAndRead()
    {
        var accessor = new ChatSessionContextAccessor { SessionId = "session-1" };

        Assert.Equal("session-1", accessor.SessionId);
    }
}
