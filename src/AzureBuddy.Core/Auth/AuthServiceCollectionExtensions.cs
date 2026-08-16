using AzureBuddy.Core.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBuddyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();

        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        // SmtpEmailSender only if an admin/deployer has actually filled in Email:Smtp:Host - otherwise
        // stay on the logging no-op (see NoOpEmailSender's docs) rather than register a sender that
        // would just fail on every send against an empty host. This mirrors how LlmSettingsProvider
        // falls back to appsettings.json defaults until something more specific is configured.
        var smtpHost = configuration[$"{SmtpOptions.SectionName}:Host"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, NoOpEmailSender>();
        }

        return services;
    }
}
