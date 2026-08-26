using System.Text;
using AzureBuddy.Core.Auth;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace AzureBuddy.Api.Extensions;

public static class JwtAuthServiceCollectionExtensions
{
    /// <summary>
    /// Wires up ASP.NET Core Identity (for user/role storage and password hashing) plus JWT bearer
    /// authentication (for validating access tokens minted by TokenService), and makes every endpoint
    /// require an authenticated user by default.
    /// </summary>
    public static IServiceCollection AddAzureBuddyJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // AddIdentityCore (not the full AddIdentity) deliberately - the full version also wires up
        // cookie authentication and SignInManager for server-rendered login pages, neither of which
        // this API uses (JWT bearer tokens only, no server-side session state). AddIdentityCore gives
        // us UserManager, password hashing/verification, and lockout tracking without that extra
        // baggage.
        //
        // .AddRoles<IdentityRole>() adds RoleManager and lets UserManager.GetRolesAsync/AddToRoleAsync
        // work - the AspNetRoles/AspNetUserRoles tables already existed in the schema from day one
        // (IdentityDbContext always includes them), they were just unused. This is what makes
        // [Authorize(Roles = "Admin")] on LlmSettingsController mean anything - without it, ASP.NET
        // Core would reject that attribute at startup because nothing would ever populate a role claim
        // to check it against.
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        // Password/lockout policy bound from config instead of hardcoded, so it can be tightened per
        // environment without a code change. See appsettings.json's "Identity" section for the defaults.
        services.Configure<IdentityOptions>(configuration.GetSection("Identity"));

        // Bound through JwtOptions (not raw jwtSection["Issuer"]/["Audience"] indexers) so validation
        // uses the SAME Issuer/Audience defaults ("AzureBuddy") that TokenService.CreateAccessToken
        // mints tokens with. The indexer approach used to return null when Jwt:Issuer/Jwt:Audience
        // weren't explicitly set in config, while TokenService's JwtOptions.Audience/.Issuer still
        // defaulted to "AzureBuddy" - every freshly-issued token then failed IDX10208 (ValidAudience is
        // null) the instant it was validated, because nothing was reading the same default the token
        // was actually stamped with.
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var jwtOptionsForValidation = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
        var jwtSigningKey = jwtSection["SigningKey"]
            ?? throw new InvalidOperationException("Missing Jwt:SigningKey in configuration.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptionsForValidation.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptionsForValidation.Audience,
                    ValidateLifetime = true,
                    // A small ClockSkew (default is 5 minutes) means an access token can still be
                    // accepted briefly after its stated expiry - fine for most APIs, but worth knowing
                    // about if you ever need "expires exactly on time" guarantees.
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey))
                };
            });

        // FallbackPolicy = every endpoint requires an authenticated user UNLESS it explicitly opts out
        // with [AllowAnonymous] (only AuthController does). This is what makes "all existing/new
        // endpoints require auth by default" hold even for anything added later without someone
        // remembering to add [Authorize].
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }
}
