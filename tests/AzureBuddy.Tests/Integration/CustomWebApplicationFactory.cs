using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Llm;
using AzureBuddy.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using DotNet.Testcontainers.Containers;
using MySqlConnector;
using StackExchange.Redis;
using Testcontainers.MySql;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// Boots the real ASP.NET Core pipeline (real routing, real [Authorize]/JWT validation, real
/// controllers/services) via WebApplicationFactory&lt;Program&gt;, against a real MySQL database (a
/// Testcontainers-managed container - see SharedMySqlContainer) rather than EF Core's InMemory
/// provider: InMemory doesn't enforce MySQL's SQL dialect, constraints, or string-comparison behavior,
/// so a test could pass against InMemory and still fail (or behave subtly differently, e.g. around
/// case-insensitive email lookups) against production's real database. A few other things are swapped
/// out so tests don't need a live Azure DevOps org or a real mailbox:
///   1. AppDbContext's connection string points at a uniquely-named database on the shared MySQL
///      container instead of Program.cs's own (CI-unavailable) configuration value.
///   2. IAdoClient is replaced with FakeAdoClient (exposed via the AdoClient property) so tests can
///      configure ADO responses per-scenario without any network call.
///   3. IEmailSender is replaced with FakeEmailSender (exposed via the EmailSender property) so tests
///      can read the password-reset/confirmation link straight out of a captured email body.
/// Everything else (Identity, JWT bearer auth, Data Protection, rate limiting, the exception handler)
/// runs exactly as it does in production - this is what makes these "integration" rather than "unit"
/// tests: they exercise the real DI wiring and HTTP pipeline, not a hand-built subset of it.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName;
    private readonly bool _useRedis;
    private readonly string _redisInstanceName;
    private string _connectionString = string.Empty;
    private IConnectionMultiplexer? _redisMultiplexer;

    public FakeAdoClient AdoClient { get; } = new();
    public FakeEmailSender EmailSender { get; } = new();
    public FakeChatCompletionClient ChatClient { get; } = new();

    // Must stay parameterless: xUnit requires every IClassFixture type to have exactly one public
    // constructor AND instantiates it itself via reflection with no arguments, so even an optional
    // `bool useRedis = false` parameter here breaks every IClassFixture<CustomWebApplicationFactory>
    // test class in the suite ("unresolved constructor arguments: Boolean useRedis"). CreateWithRedis
    // and CreateReplicaOf below are the public entry points for every other case.
    public CustomWebApplicationFactory() : this($"azurebuddy_test_{Guid.NewGuid():N}", string.Empty, useRedis: false, $"test:{Guid.NewGuid():N}:")
    {
    }

    private CustomWebApplicationFactory(string databaseName, string connectionString, bool useRedis, string redisInstanceName)
    {
        _databaseName = databaseName;
        _connectionString = connectionString;
        _useRedis = useRedis;
        _redisInstanceName = redisInstanceName;
    }

    /// <summary>Like `new CustomWebApplicationFactory()`, but IChatHistoryStore is backed by a real
    /// Redis container (SharedRedisContainer) instead of the process-local in-memory fallback - needed
    /// by any test that wants to prove behavior that only exists when Redis is configured, e.g.
    /// cross-replica chat continuity (see CreateReplicaOf below).</summary>
    public static CustomWebApplicationFactory CreateWithRedis() =>
        new($"azurebuddy_test_{Guid.NewGuid():N}", string.Empty, useRedis: true, $"test:{Guid.NewGuid():N}:");

    /// <summary>
    /// Builds a second host that shares <paramref name="shareInfraWith"/>'s database and (when that
    /// factory was created via CreateWithRedis) its Redis key prefix, so the two factories behave like
    /// two replicas of the SAME deployment behind a load balancer rather than two unrelated tests'
    /// isolated infra. <paramref name="shareInfraWith"/> must already have completed InitializeAsync
    /// before this is called, since that's what populates the database connection string being copied here.
    /// </summary>
    public static CustomWebApplicationFactory CreateReplicaOf(CustomWebApplicationFactory shareInfraWith) =>
        new(shareInfraWith._databaseName, shareInfraWith._connectionString, shareInfraWith._useRedis, shareInfraWith._redisInstanceName);

    /// <summary>
    /// Runs once per CustomWebApplicationFactory instance (i.e. once per test class that uses
    /// IClassFixture&lt;CustomWebApplicationFactory&gt;, plus once for each standalone
    /// `new CustomWebApplicationFactory()` a test constructs for its own isolated database - see
    /// LlmSettingsEndpointsTests). It never starts a new container itself; it only asks the one shared
    /// container for a fresh, uniquely-named database, then applies real EF Core migrations against it
    /// (Database.MigrateAsync, not EnsureCreatedAsync) so the migrations themselves - not just the model
    /// they produce - are exercised by the test suite.
    ///
    /// Migrations run against a standalone AppDbContext built directly from a DbContextOptionsBuilder,
    /// deliberately NOT through `Services` - accessing `Services`/`Server` is what triggers
    /// WebApplicationFactory to actually build AND START the host, and Program.cs's own startup code
    /// (the admin-role-seeding block, which queries AspNetRoles) runs synchronously as part of that,
    /// before control ever returns here. Every CI run so far failed with "Table ... AspNetRoles
    /// doesn't exist" precisely because that startup query ran before this method's migration call
    /// got a chance to create it - triggering the host build only after migrations have already
    /// completed avoids the ordering problem entirely, rather than trying to win a race against it.
    /// </summary>
    public async Task InitializeAsync()
    {
        // A blank _connectionString means this is either the first factory to touch _databaseName, or
        // a standalone factory that isn't sharing infra with anyone - either way it owns creating and
        // migrating the database. A factory built via the infra-sharing constructor already has this
        // populated (copied from the factory it shares with), so it skips straight past both: the
        // database already exists and was already migrated by whichever factory created it first.
        if (string.IsNullOrEmpty(_connectionString))
        {
            var container = await SharedMySqlContainer.GetAsync();
            _connectionString = await CreateIsolatedDatabaseAsync(container, _databaseName);

            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 34)));
            await using var dbContext = new AppDbContext(optionsBuilder.Options);
            await dbContext.Database.MigrateAsync();
        }

        if (_useRedis)
        {
            var redisContainer = await SharedRedisContainer.GetAsync();
            _redisMultiplexer = await ConnectionMultiplexer.ConnectAsync(redisContainer.GetConnectionString());
        }
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _redisMultiplexer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test isolation strategy: rather than wrapping each test in a rolled-back transaction (this
    /// codebase's tests exercise full HTTP round-trips through AuthController/AccountController etc.,
    /// which commit their own SaveChangesAsync calls independently - there's no single ambient
    /// transaction to wrap) or reseeding tables between tests, every CustomWebApplicationFactory
    /// instance gets its own MySQL database, created fresh here and never reused. Combined with the
    /// existing convention of unique per-test data (RegisterAsync defaults to a random
    /// `user-{Guid}@example.com`), this means tests within a class can run in any order without
    /// colliding, and tests in different classes are on entirely separate schemas - the same isolation
    /// shape the previous EF InMemory provider gave for free, just against a real server.
    ///
    /// This has to run as MySQL's root user, not the app's own DB user: the official MySQL image's
    /// entrypoint (which SharedMySqlContainer's MYSQL_USER/MYSQL_DATABASE env vars feed into) only
    /// grants that user privileges on the ONE database named at container startup - it has no CREATE
    /// DATABASE privilege, so opening a connection with the app's own credentials and issuing CREATE
    /// DATABASE fails with "Access denied". Testcontainers.MySql's ExecScriptAsync looked like the
    /// answer, but its source (Testcontainers.MySql.MySqlContainer.ExecScriptAsync) writes the app
    /// user's own credentials into an in-container my.cnf and runs the mysql CLI against those - same
    /// unprivileged user, same "Access denied" failure, just moved one layer down. What actually has
    /// root's privileges is root itself: MySqlBuilder.WithPassword sets MYSQL_ROOT_PASSWORD to the SAME
    /// value passed for the app user's password, so root's password is already sitting in the
    /// connection string GetConnectionString() returns - no separate secret to construct or guess. We
    /// exec the mysql CLI as -uroot with that password directly (via IContainer.ExecAsync, which runs
    /// the argument list as-is with no shell in between - no bash quoting of the SQL's backtick
    /// identifier quotes to worry about, unlike a `bash -lc "..."` wrapper).
    /// </summary>
    private static async Task<string> CreateIsolatedDatabaseAsync(MySqlContainer container, string databaseName)
    {
        var connectionStringBuilder = new MySqlConnectionStringBuilder(container.GetConnectionString());

        // databaseName is always our own Guid-based name (never user input), appUser comes from our own
        // SharedMySqlContainer setup (not user input either), and rootPassword is that same setup's
        // password (see the method doc above) - so string interpolation into the SQL here isn't a
        // SQL-injection concern. MySQL also doesn't support parameterizing identifiers like a database
        // or user name.
        var appUser = connectionStringBuilder.UserID;
        var rootPassword = connectionStringBuilder.Password;

        await container.ExecAsync(new[]
        {
            "mysql",
            "-uroot",
            $"-p{rootPassword}",
            "-e",
            $"CREATE DATABASE `{databaseName}`; GRANT ALL PRIVILEGES ON `{databaseName}`.* TO '{appUser}'@'%';"
        }).ThrowOnFailure();

        connectionStringBuilder.Database = databaseName;
        return connectionStringBuilder.ConnectionString;
    }

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
            services.AddDbContext<AppDbContext>(options =>
                options.UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 34))));

            services.RemoveAll<IAdoClient>();
            services.AddSingleton<IAdoClient>(AdoClient);

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);

            services.RemoveAll<IChatCompletionClient>();
            services.AddSingleton<IChatCompletionClient>(ChatClient);

            if (_useRedis)
            {
                // RedisOptions.InstanceName is init-only, so it can't be mutated through Configure<T>'s
                // Action<T> delegate - registering our own IOptions<RedisOptions> singleton after
                // Program.cs's own config-bound one is what actually wins, since DI resolves the LAST
                // registration for a non-keyed, non-IEnumerable dependency.
                services.AddSingleton<IOptions<RedisOptions>>(Options.Create(new RedisOptions { InstanceName = _redisInstanceName }));
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(_redisMultiplexer!);
                services.RemoveAll<IChatHistoryStore>();
                services.AddScoped<IChatHistoryStore, RedisChatHistoryStore>();
            }
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
