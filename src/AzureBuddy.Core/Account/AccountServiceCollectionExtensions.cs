using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Account;

public static class AccountServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBuddyAccount(this IServiceCollection services)
    {
        services.AddScoped<AccountService>();
        return services;
    }
}
