using AzureBuddy.Api.Startup;
using AzureBuddy.Core.Llm;

namespace AzureBuddy.Api.Extensions;

public static class StartupTasksExtensions
{
    /// <summary>
    /// There's no in-app "invite an admin" flow (out of scope for this pass) - this is the
    /// bootstrapping mechanism instead: ensure the "Admin" role exists, then grant it to any user
    /// whose email appears in Admin:Emails (see appsettings.json). The actual work lives in
    /// AdminRoleSeeder (see AzureBuddy.Api/Startup/AdminRoleSeeder.cs) so it's independently
    /// testable/DI-resolvable; this just runs it in its own scope (services registered at
    /// Scoped/Transient lifetime aren't available on the root IServiceProvider `app.Services`
    /// directly) with a retry.
    ///
    /// Wrapped in StartupRetry: without it, MySQL being briefly unreachable at the exact moment this
    /// container starts (a common race in orchestrated deployments where the DB and app start together)
    /// used to throw straight out of Program.cs's top-level statements and kill the process before
    /// app.Run() ever executed - so even /health/live never came up to report what had gone wrong.
    /// </summary>
    public static async Task SeedAdminRolesAsync(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBuddy.Startup");
        await StartupRetry.RunAsync("Admin role seeding", logger, async cancellationToken =>
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminRoleSeeder>().SeedAsync(cancellationToken);
        }, CancellationToken.None);
    }

    /// <summary>
    /// LlmSettingsProvider (see AzureBuddy.Core/Llm/ILlmSettingsProvider.cs) already seeded itself from
    /// appsettings.json's Llm section when DI first constructed it. This overrides that with the
    /// database row's values, if one exists - the same "database wins over the config file, once
    /// someone has actually saved something" precedence LlmSettingsService.GetAsync uses when deciding
    /// what to show an admin. A no-op if no row exists yet (a brand new deployment), leaving the
    /// appsettings.json values in effect exactly as before this feature existed.
    ///
    /// Wrapped in StartupRetry for the same reason as SeedAdminRolesAsync above - see that doc comment.
    /// </summary>
    public static async Task LoadLlmSettingsFromDatabaseAsync(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureBuddy.Startup");
        await StartupRetry.RunAsync("LLM settings load", logger, async cancellationToken =>
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LlmSettingsService>().LoadFromDatabaseIfPresentAsync(cancellationToken);
        }, CancellationToken.None);
    }
}
