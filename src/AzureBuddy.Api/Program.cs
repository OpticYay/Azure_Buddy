using System.Text.Json.Serialization;
using AzureBuddy.Api;
using AzureBuddy.Api.Extensions;
using AzureBuddy.Api.Startup;
using AzureBuddy.Core.Account;
using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Routing;
using AzureBuddy.Core.Settings;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// ---- Redis (optional) ----
// Constructed here via a static factory - not a DI registration - because the Data Protection builder
// further down needs the live IConnectionMultiplexer at configuration time, before the service
// container is built. `using var` on a top-level statement disposes at the end of the implicit Main
// method, i.e. at process shutdown (app.Run() blocks until then), so this bootstrap logger factory
// stays alive for ConnectionFailed/ConnectionRestored events firing at any point during the run - not
// just the ones logged during startup.
using var redisLoggerFactory = LoggerFactory.Create(logging =>
    logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole());
var redisMultiplexer = RedisConnectionFactory.TryCreate(builder.Configuration, redisLoggerFactory);
if (redisMultiplexer is null)
{
    // Loud on purpose: dotnet run and every WebApplicationFactory-based integration test are expected
    // to hit this path (no Redis available), so it's not an error - but a real deployment silently
    // running single-instance with no idea why a second replica behaves incorrectly is exactly the
    // failure mode this whole Redis effort exists to fix.
    redisLoggerFactory.CreateLogger("AzureBuddy.Redis").LogWarning(
        "Redis:Configuration is not set - chat history, Data Protection keys, and LLM settings changes " +
        "stay in-memory/per-instance. This deployment cannot be scaled beyond one API replica.");
}

builder.Services.AddAzureBuddyRedis(builder.Configuration, redisMultiplexer);

builder.Services.AddControllers()
    // Without this, every C# enum (ChatMessageRole, ChatMessageType) serializes as its underlying int
    // (0, 1, ...) by System.Text.Json's default behavior - forcing every API consumer to hardcode a
    // number-to-name mapping themselves (which the frontend was doing: `{User: 0, Assistant: 1}`) with
    // no compile-time link back to what those numbers actually mean. Serializing as the member's name
    // ("User", "Assistant") instead is self-describing and matches how every other field in this API's
    // JSON is already named.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Model-validation failures (e.g. SaveAdoSettingsRequest's [Required]/[Url] attributes) are normally
// turned into ASP.NET Core's own ValidationProblemDetails shape automatically by [ApiController] -
// yet another error shape, different from both AuthController's and GlobalExceptionHandler's. This
// override makes THAT automatic response use the same ApiErrorResponse shape as everywhere else,
// so truly every error response in this API - however it originates - has one consistent body.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiError(
                "validation_error",
                error.ErrorMessage,
                System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(entry.Key))))
            .ToList();

        return new BadRequestObjectResult(new ApiErrorResponse(errors));
    };
});

builder.Services.AddAzureBuddyCors(builder.Configuration, builder.Environment);
builder.Services.AddAzureBuddySwagger();

// ---- Global exception handling ----
// Without this, any exception that escapes a controller/service falls through to the framework
// default - a stack-trace HTML page in Development, a bare empty 500 in Production - with no
// consistent shape for API clients to parse. The IExceptionHandler below gives every unhandled
// exception the same ApiErrorResponse JSON body regardless of where it was thrown.
//
// AddProblemDetails() is still needed here even though GlobalExceptionHandler.TryHandleAsync always
// returns true (i.e. our handler always handles it, ProblemDetails's own format is never actually
// produced) - app.UseExceptionHandler() validates at startup that SOME fallback exists for the
// hypothetical case where every registered IExceptionHandler declines to handle an exception, and
// throws on startup if nothing is registered to cover that case. It's a required safety net, not a
// second, competing error shape.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---- Database ----
// MySQL via the Pomelo EF Core provider. ServerVersion.Create with an explicit version (rather than
// ServerVersion.AutoDetect, which opens a connection at startup just to ask the server its version)
// keeps app startup from depending on network round-trips before it's even accepting requests - if
// your MySQL server is a different version, update this to match.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection in configuration.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 34))));

builder.Services.AddAzureBuddyJwtAuth(builder.Configuration);
builder.Services.AddAzureBuddyRateLimiting(builder.Configuration, builder.Environment.IsEnvironment("Testing"));

// ---- Data Protection (PAT/API-key encryption key storage) ----
// By default (and always, when Redis isn't configured), Data Protection keys live under an explicit
// filesystem directory - which breaks the moment this app scales to more than one replica, since each
// instance would generate its own key ring and be unable to decrypt ciphertext another instance
// produced (LlmSettingsService.BuildEffectiveOptions and UserAdoConfigService.GetConnectionContextAsync
// both now degrade gracefully rather than 500ing when that happens, but "please re-enter your PAT" for
// every user on every deploy is still exactly the failure this fixes). When Redis IS configured, every
// replica instead persists to and reads from the SAME Redis-backed key ring via
// PersistKeysToStackExchangeRedis, so a PAT/API key encrypted by one replica decrypts fine on any other.
// See the README for what happens if this key material is ever lost (every stored PAT/API key becomes
// permanently unreadable and users must re-enter them) and DataProtectionKeyImporter for migrating an
// existing filesystem key ring into Redis during a one-time cutover.
var dataProtectionBuilder = builder.Services.AddDataProtection().SetApplicationName("AzureBuddy");
if (redisMultiplexer is not null)
{
    var redisInstanceName = builder.Configuration[$"{RedisOptions.SectionName}:InstanceName"] ?? "azurebuddy:";
    dataProtectionBuilder.PersistKeysToStackExchangeRedis(redisMultiplexer, $"{redisInstanceName}DataProtection-Keys");
}
else
{
    var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"] ?? "DataProtection-Keys";
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
}

builder.Services.AddLlmProviders(builder.Configuration, redisMultiplexer);
builder.Services.AddAzureDevOps(builder.Configuration);
builder.Services.AddWorkItemStates();
builder.Services.AddChatRouting();
builder.Services.AddAzureBuddyAgent(redisMultiplexer);
builder.Services.AddAzureBuddyAuth(builder.Configuration);
builder.Services.AddAzureBuddyAccount();
builder.Services.AddAdoSettings();
builder.Services.AddChatHistory();
builder.Services.AddScoped<AdminRoleSeeder>();

// ---- Health checks ----
// "ready" tags DatabaseHealthCheck (and RedisHealthCheck, only when Redis is configured) so
// /health/ready reflects whether this replica can actually serve a request right now; /health/live
// below runs no checks at all - see the two MapHealthChecks calls after the pipeline is built for why
// that split matters for how an orchestrator should react to each one failing.
builder.Services.AddHealthChecks()
    .AddCheck<AzureBuddy.Api.HealthChecks.DatabaseHealthCheck>("database", tags: new[] { "ready" });
if (redisMultiplexer is not null)
{
    builder.Services.AddHealthChecks()
        .AddCheck<AzureBuddy.Api.HealthChecks.RedisHealthCheck>("redis", tags: new[] { "ready" });
}

var app = builder.Build();

await app.SeedAdminRolesAsync();
await app.LoadLlmSettingsFromDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Tells browsers to remember (via the Strict-Transport-Security header) to always use HTTPS for
    // this origin going forward, closing the window where a first request over plain HTTP could be
    // intercepted before UseHttpsRedirection below gets a chance to redirect it. Skipped in Development
    // to avoid caching issues with self-signed certs, matching the standard ASP.NET Core template.
    app.UseHsts();
}

// Must run before anything that reads Connection.RemoteIpAddress or Request.Scheme - the rate
// limiter's per-user/per-IP partitioning and UseHttpsRedirection below both depend on this having
// already rewritten them from X-Forwarded-For/X-Forwarded-Proto (when Network:KnownProxies trusts the
// caller).
app.UseForwardedHeaders();

// Registered as early as possible (only UseForwardedHeaders runs first, and it never throws) so it
// wraps the entire request pipeline - any exception from any downstream middleware or controller gets
// caught here.
app.UseExceptionHandler();

app.UseHttpsRedirection();

// Must run before UseAuthentication/UseAuthorization - the browser's CORS preflight (OPTIONS)
// request carries no Authorization header, so if this ran later the preflight itself would get
// rejected by the auth pipeline before CORS ever got a chance to approve the real request.
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Runs after UseAuthorization (the standard ASP.NET Core ordering: routing, CORS, auth, then rate
// limiting) specifically so ChatRateLimiterPolicy's per-user partitioning can read the verified user id
// claim - if this ran earlier (as it used to, before CORS), HttpContext.User would still be anonymous
// and per-user partitioning would be impossible. AuthRateLimiterPolicy's per-IP partitioning for
// register/login/refresh is unaffected by the move, since RemoteIpAddress is available regardless of
// where in the pipeline this runs.
app.UseRateLimiter();

app.MapControllers();

// AllowAnonymous is required on both: AddAzureBuddyJwtAuth's AuthorizationOptions.FallbackPolicy
// requires an authenticated user on every endpoint that doesn't opt out, and an orchestrator's health
// probe never carries a bearer token.
//
// /health/live runs no checks (Predicate = _ => false) - it only proves the process is up and the
// pipeline can complete a request, which is all a liveness probe should ask: failing it tells an
// orchestrator to kill and restart the container, so it must never fail for a reason a restart can't
// fix (like MySQL being briefly unreachable).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

// /health/ready runs every check tagged "ready" (DatabaseHealthCheck, and RedisHealthCheck when Redis
// is configured) - failing it tells a load balancer to stop routing traffic to this replica without
// restarting it, the correct reaction to "a dependency is down" rather than "this process is broken."
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

// Top-level statements generate an internal "Program" class by default. Making it public (partial,
// empty - it's purely a marker) is the standard ASP.NET Core pattern for letting integration tests
// use WebApplicationFactory<Program> to spin up this exact app in-memory with swapped-out config/DI
// (e.g. an in-memory database instead of real MySQL) - see tests/AzureBuddy.Tests/Integration/.
public partial class Program;
