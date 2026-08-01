using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Chat;

public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddChatHistory(this IServiceCollection services)
    {
        services.AddScoped<ChatSessionService>();
        return services;
    }
}
