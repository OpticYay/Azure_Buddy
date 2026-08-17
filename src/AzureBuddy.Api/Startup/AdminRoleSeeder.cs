using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity;

namespace AzureBuddy.Api.Startup;

/// <summary>Bootstraps the "Admin" role and grants it to any existing user whose email appears in
/// Admin:Emails (see appsettings.json). There's no in-app "invite an admin" flow (out of scope for this
/// pass) - this promotes an existing account, it doesn't create one: register normally through the UI,
/// add that email to Admin:Emails, restart the app once.
///
/// Runs on every startup and is idempotent (AddToRoleAsync on a user who already has the role is a safe
/// no-op via Identity's own duplicate check), so redeploying doesn't create duplicate role assignments or
/// throw if the list hasn't changed.
///
/// Wrapped in a retry by the caller (see Program.cs's StartupRetry) rather than here, so the same retry
/// helper also covers LlmSettingsService.LoadFromDatabaseIfPresentAsync - both run in the same
/// "MySQL might not be reachable yet" startup window.</summary>
public sealed class AdminRoleSeeder
{
    private const string AdminRole = "Admin";

    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminRoleSeeder> _logger;

    public AdminRoleSeeder(
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<AdminRoleSeeder> logger)
    {
        _roleManager = roleManager;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!await _roleManager.RoleExistsAsync(AdminRole))
        {
            await _roleManager.CreateAsync(new IdentityRole(AdminRole));
        }

        var adminEmails = _configuration.GetSection("Admin:Emails").Get<string[]>() ?? Array.Empty<string>();
        foreach (var email in adminEmails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var user = await _userManager.FindByEmailAsync(email);
            if (user is null)
            {
                _logger.LogWarning("Admin:Emails lists {Email}, but no user with that email exists yet.", email);
                continue;
            }

            if (!await _userManager.IsInRoleAsync(user, AdminRole))
            {
                await _userManager.AddToRoleAsync(user, AdminRole);
            }
        }
    }
}
