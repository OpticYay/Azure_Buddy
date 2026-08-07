using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// Boots the real ASP.NET Core pipeline (real routing, real [Authorize]/JWT validation, real
/// controllers/services) in-memory via WebApplicationFactory&lt;Program&gt;, with a few things swapped
/// out so tests don't need a live MySQL server, a live Azure DevOps org, or a real mailbox:
///   1. AppDbContext's provider is switched from MySQL to EF Core's InMemory provider.
///   2. IAdoClient is replaced with FakeAdoClient (exposed via the AdoClient property) so tests can
///      configure ADO responses per-scenario without any network call.
///   3. IEmailSender is replaced with FakeEmailSender (exposed via the EmailSender property) so tests
///      can read the password-reset/confirmation link straight out of a captured email body.
/// Everything else (Identity, JWT bearer auth, Data Protection, rate limiting, the exception handler)
/// runs exactly as it does in production - this is what makes these "integration" rather than "unit"
/// tests: they exercise the real DI wiring and HTTP pipeline, not a hand-built subset of it.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"AzureBuddyTests-{Guid.NewGuid()}";

    public FakeAdoClient AdoClient { get; } = new();
    public FakeEmailSender EmailSender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs relaxes the auth rate limiter specifically for this environment name - see the
        // comment there for why (TestServer requests all share one synthetic client IP).
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // AddDbContext uses TryAdd internally, so Program.cs's own MySQL registration (already in
            // the collection by the time this callback runs) has to be explicitly removed before a
            // replacement will take effect - simply calling AddDbContext again here would be a no-op.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IAdoClient>();
            services.AddSingleton<IAdoClient>(AdoClient);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        });
    }

    /// <summary>
    /// Requests through TestServer default to an http:// base address, which trips Program.cs's real
    /// app.UseHttpsRedirection() middleware into issuing a 307 before the request ever reaches a
    /// controller. Using an https:// base address avoids that redirect entirely (TestServer doesn't do
    /// real TLS - it only inspects the request URI's scheme to populate HttpContext.Request.IsHttps).
    /// </summary>
    public HttpClient CreateHttpsClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}
