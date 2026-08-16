using AzureBuddy.Core.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Chat;

public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddChatHistory(this IServiceCollection services)
    {
        services.AddScoped<ChatSessionService>();
        // Scoped, matching the AppDbContext it wraps - see DbChatHistorySource's own doc comment for
        // why this can't live on ChatSessionService instead (a DI cycle through IChatHistoryStore).
        services.AddScoped<IChatHistorySource, DbChatHistorySource>();
        return services;
    }
}
