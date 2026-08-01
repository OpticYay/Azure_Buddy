using AzureBuddy.Core.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBuddyAgent(this IServiceCollection services)
    {
        services.AddSingleton<ChatHistoryStore>();
        services.AddScoped<AdoWorkItemToolset>();
        services.AddScoped<ToolCatalog>();
        services.AddScoped<IConversationalAgent, AzureBuddyAgent>();
        return services;
    }
}
