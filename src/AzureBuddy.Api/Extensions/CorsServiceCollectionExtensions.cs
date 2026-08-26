using Microsoft.Extensions.Hosting;

namespace AzureBuddy.Api.Extensions;

public static class CorsServiceCollectionExtensions
{
    /// <summary>
    /// The browser blocks JS on one origin (e.g. the Angular dev server at http://localhost:4200) from
    /// reading responses from a different origin (this API, e.g. http://localhost:5013) unless the
    /// server explicitly opts in via CORS headers - a browser security default, not something this app
    /// chooses. AllowCredentials is needed because the Angular app sends "Authorization: Bearer
    /// &lt;token&gt;" (an Authorization header counts as a credentialed request for CORS purposes even
    /// without cookies), and AllowCredentials cannot be combined with AllowAnyOrigin - the origin list
    /// must be explicit. Both spellings of the dev server's own address are allowed because either can
    /// be what's in the browser's address bar, and the browser sends whichever one it used as the
    /// Origin header - a mismatch there is a blocked request, not a fallback. Nothing here depends on
    /// which one you use.
    ///
    /// The localhost fallback is deliberately Development-only: a deployer who forgets to set
    /// Cors:AllowedOrigins in a real environment gets a loud startup failure (matching the existing
    /// pattern for ConnectionStrings:DefaultConnection and Jwt:SigningKey), not a CORS policy that
    /// silently only allows localhost:4200 and makes the real frontend origin unreachable with no error
    /// anywhere except the browser's console.
    /// </summary>
    public static IServiceCollection AddAzureBuddyCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredCorsOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        var corsOrigins = configuredCorsOrigins
            ?? (environment.IsDevelopment()
                ? new[] { "http://localhost:4200", "http://127.0.0.1:4200" }
                : throw new InvalidOperationException(
                    "Missing Cors:AllowedOrigins in configuration. This is required outside Development so the " +
                    "API doesn't silently fall back to allowing only localhost origins."));

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy => policy
                .WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        });

        return services;
    }
}
