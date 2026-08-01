using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Routing;

public static class RoutingServiceCollectionExtensions
{
    public static IServiceCollection AddChatRouting(this IServiceCollection services)
    {
        services.AddScoped<IntentExtractor>();
        services.AddScoped<CreateBugFlow>();
        services.AddScoped<ViewBugsFlow>();
        services.AddScoped<UpdateItemFlow>();
        services.AddScoped<MyItemsFlow>();
        services.AddScoped<IntentRouter>();
        return services;
    }
}
