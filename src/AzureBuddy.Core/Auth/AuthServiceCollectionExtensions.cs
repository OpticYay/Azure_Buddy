using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBuddyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();

        // No real email provider yet (see IEmailSender's docs) - registering the no-op keeps anything
        // that depends on IEmailSender resolvable without pulling in SMTP/SendGrid config prematurely.
        services.AddSingleton<IEmailSender, NoOpEmailSender>();

        return services;
    }
}
