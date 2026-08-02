using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Settings;

public static class SettingsServiceCollectionExtensions
{
    public static IServiceCollection AddAdoSettings(this IServiceCollection services)
    {
        services.AddScoped<PatProtector>();
        services.AddScoped<UserAdoConfigService>();
        return services;
    }
}
