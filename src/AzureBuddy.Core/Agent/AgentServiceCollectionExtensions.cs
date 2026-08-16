using AzureBuddy.Core.Routing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AzureBuddy.Core.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// <paramref name="multiplexer"/> mirrors the null-means-"Redis not configured" convention
    /// AddAzureBuddyRedis already uses: pass the same instance (or null) Program.cs got back from
    /// RedisConnectionFactory.TryCreate. When present, IChatHistoryStore is Redis-backed so every
    /// replica shares one conversation window per session; when absent, it falls back to the
    /// process-local InMemoryChatHistoryStore, which is also what every WebApplicationFactory-based
    /// test that doesn't opt into a Redis container gets.
    /// </summary>
    public static IServiceCollection AddAzureBuddyAgent(this IServiceCollection services, IConnectionMultiplexer? multiplexer)
    {
        if (multiplexer is not null)
        {
            services.AddScoped<IChatHistoryStore, RedisChatHistoryStore>();
        }
        else
        {
            // Singleton: the shared, process-lifetime backing state for InMemoryChatHistoryStore (see
            // its own doc comment). The store itself is Scoped - it depends on the Scoped
            // IChatHistorySource (backed by AppDbContext) - so the shared dictionary has to live in a
            // separate singleton rather than on the store, the same split RedisChatHistoryStore uses for
            // IConnectionMultiplexer.
            services.AddSingleton<InMemoryChatWindowCache>();
            services.AddScoped<IChatHistoryStore, InMemoryChatHistoryStore>();
        }

        services.AddScoped<AdoWorkItemToolset>();
        services.AddScoped<ToolCatalog>();
        services.AddScoped<IConversationalAgent, AzureBuddyAgent>();
        return services;
    }
}
