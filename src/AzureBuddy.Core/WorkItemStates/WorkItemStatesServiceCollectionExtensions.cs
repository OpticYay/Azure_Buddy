using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.WorkItemStates;

public static class WorkItemStatesServiceCollectionExtensions
{
    public static IServiceCollection AddWorkItemStates(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<WorkItemStateConfigService>();
        return services;
    }
}
