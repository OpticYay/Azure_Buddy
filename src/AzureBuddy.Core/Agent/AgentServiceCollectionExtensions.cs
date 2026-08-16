using AzureBuddy.Core.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBuddyAgent(this IServiceCollection services)
    {
        // Singleton: the shared, process-lifetime backing state for InMemoryChatHistoryStore (see its
        // own doc comment). The store itself is Scoped - it depends on the Scoped IChatHistorySource
        // (backed by AppDbContext) - so the shared dictionary has to live in a separate singleton rather
        // than on the store, the same split RedisChatHistoryStore will use for IConnectionMultiplexer.
        services.AddSingleton<InMemoryChatWindowCache>();
        services.AddScoped<IChatHistoryStore, InMemoryChatHistoryStore>();
        services.AddScoped<AdoWorkItemToolset>();
        services.AddScoped<ToolCatalog>();
        services.AddScoped<IConversationalAgent, AzureBuddyAgent>();
        return services;
    }
}
