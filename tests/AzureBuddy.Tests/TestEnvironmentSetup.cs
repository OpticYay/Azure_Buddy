using System.Runtime.CompilerServices;

namespace AzureBuddy.Tests;

/// <summary>
/// Program.cs reads ConnectionStrings:Default and Jwt:SigningKey directly off configuration during its
/// own top-level statements, before WebApplicationFactory's ConfigureWebHost customizations ever get a
/// chance to run - confirmed by reproducing this against a clean checkout: those customizations are
/// only applied once the host is actually built, which happens after Program.cs's own reads. Locally
/// this is invisible because a real (gitignored) appsettings.json supplies both values, but a fresh CI
/// checkout has neither file, so Program.cs throws "Missing ConnectionStrings:Default in configuration"
/// before a single test can run.
///
/// A ModuleInitializer runs once, before any test in this assembly executes, and sets these as real
/// process environment variables (ASP.NET Core's config system reads "Jwt__SigningKey" as
/// "Jwt:SigningKey") - early enough to satisfy Program.cs's own checks regardless of what local files
/// exist. Environment variables also outrank appsettings.json in the default configuration provider
/// order, so this makes test runs hermetic even on a machine that DOES have a local appsettings.json:
/// tests never depend on a developer's personal secrets.
///
/// ConnectionStrings:Default only needs to be non-empty here - CustomWebApplicationFactory swaps the
/// real DbContext registration for one pointing at the Testcontainers MySQL instance before this value
/// is ever used to open a connection. Jwt:SigningKey/Issuer/Audience, by contrast, DO matter: real
/// tokens are minted and validated during tests (login, refresh, [Authorize] checks), so these need
/// real, consistent values for the whole run - Issuer/Audience are set explicitly (rather than left
/// unset) because JwtOptions defaults them to "AzureBuddy" when minting a token, but the JwtBearer
/// validation options in Program.cs read Jwt:Issuer/Jwt:Audience straight off configuration with no
/// such default, so leaving them unset would mint tokens whose issuer/audience nothing accepts.
/// </summary>
internal static class TestEnvironmentSetup
{
    [ModuleInitializer]
    public static void Configure()
    {
        SetIfAbsent("ConnectionStrings__Default", "Server=localhost;Port=3306;Database=unused;Uid=unused;Pwd=unused;");
        SetIfAbsent("Jwt__SigningKey", "azure-buddy-integration-test-signing-key-do-not-use-in-production");
        SetIfAbsent("Jwt__Issuer", "AzureBuddy");
        SetIfAbsent("Jwt__Audience", "AzureBuddy");
    }

    private static void SetIfAbsent(string variable, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            Environment.SetEnvironmentVariable(variable, value);
        }
    }
}
