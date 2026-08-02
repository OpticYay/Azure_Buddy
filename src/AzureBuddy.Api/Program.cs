using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AzureBuddy.Api;
using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Routing;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

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

// ---- CORS (Cross-Origin Resource Sharing) ----
// The browser blocks JS on one origin (e.g. the Angular dev server at http://localhost:4200) from
// reading responses from a different origin (this API, e.g. http://localhost:5013) unless the server
// explicitly opts in via CORS headers - a browser security default, not something this app chooses.
// AllowCredentials is needed because the Angular app sends "Authorization: Bearer <token>" (an
// Authorization header counts as a credentialed request for CORS purposes even without cookies), and
// AllowCredentials cannot be combined with AllowAnyOrigin - the origin list must be explicit.
// Both spellings of the dev server's own address are allowed because either can be what's in the
// browser's address bar, and the browser sends whichever one it used as the Origin header - a
// mismatch there is a blocked request, not a fallback. Nothing here depends on which one you use.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200", "http://127.0.0.1:4200" };

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter your JWT access token (without the word 'Bearer' - just paste the token itself)"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

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
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default in configuration.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 34))));

// ---- ASP.NET Core Identity ----
// AddIdentityCore (not the full AddIdentity) deliberately - the full version also wires up cookie
// authentication and SignInManager for server-rendered login pages, neither of which this API uses
// (JWT bearer tokens only, no server-side session state). AddIdentityCore gives us UserManager,
// password hashing/verification, and lockout tracking without that extra baggage.
//
// .AddRoles<IdentityRole>() adds RoleManager and lets UserManager.GetRolesAsync/AddToRoleAsync work -
// the AspNetRoles/AspNetUserRoles tables already existed in the schema from day one (IdentityDbContext
// always includes them), they were just unused. This is what makes [Authorize(Roles = "Admin")] on
// LlmSettingsController mean anything - without it, ASP.NET Core would reject that attribute at
// startup because nothing would ever populate a role claim to check it against.
builder.Services.AddIdentityCore<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// Password/lockout policy bound from config instead of hardcoded, so it can be tightened per
// environment without a code change. See appsettings.json's "Identity" section for the defaults.
builder.Services.Configure<IdentityOptions>(builder.Configuration.GetSection("Identity"));

// ---- JWT bearer authentication ----
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtSigningKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Missing Jwt:SigningKey in configuration.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            // A small ClockSkew (default is 5 minutes) means an access token can still be accepted
            // briefly after its stated expiry - fine for most APIs, but worth knowing about if you
            // ever need "expires exactly on time" guarantees.
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey))
        };
    });

// FallbackPolicy = every endpoint requires an authenticated user UNLESS it explicitly opts out with
// [AllowAnonymous] (only AuthController does). This is what makes "all existing/new endpoints require
// auth by default" hold even for anything added later without someone remembering to add [Authorize].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---- Rate limiting ----
// Fixed-window limiter on the auth endpoints only: 5 requests per minute per client IP. This is a
// basic brute-force/registration-spam speed bump, not a substitute for account lockout (Identity's
// lockout policy above already handles "too many wrong passwords for one account").
//
// The "Testing" environment gets an effectively unlimited permit count: WebApplicationFactory-based
// integration tests all originate from the TestServer's single synthetic client IP, so a real-world
// per-IP limit of 5/minute trips almost immediately once more than a handful of tests run against the
// same factory instance - this isn't a workaround for a bug, it's the same limiter correctly doing its
// job against traffic that (unlike real clients) has no IP diversity. Production behavior is unchanged.
var authRateLimit = builder.Environment.IsEnvironment("Testing") ? int.MaxValue : 5;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(RateLimiterPolicies.Auth, limiterOptions =>
    {
        limiterOptions.PermitLimit = authRateLimit;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

// ---- Data Protection (PAT encryption key storage) ----
// By default, Data Protection keys live under the OS user profile, which breaks the moment this app
// runs in a container or scales to more than one instance (each instance would generate its own key
// and be unable to decrypt PATs another instance encrypted). Pointing it at an explicit, persistent
// directory - and in a real multi-instance deployment, a *shared* one (mounted volume, or
// PersistKeysToDbContext/Azure Blob storage instead of the filesystem) - avoids that. See the README
// for what happens if this key material is ever lost (short version: every stored PAT becomes
// permanently unreadable and users must re-enter them).
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"] ?? "DataProtection-Keys";
builder.Services.AddDataProtection()
    .SetApplicationName("AzureBuddy")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

builder.Services.AddLlmProviders(builder.Configuration);
builder.Services.AddAzureDevOps(builder.Configuration);
builder.Services.AddChatRouting();
builder.Services.AddAzureBuddyAgent();
builder.Services.AddAzureBuddyAuth(builder.Configuration);
builder.Services.AddAdoSettings();
builder.Services.AddChatHistory();

var app = builder.Build();

// ---- Admin role seeding ----
// There's no in-app "invite an admin" flow (out of scope for this pass) - this is the bootstrapping
// mechanism instead: ensure the "Admin" role exists, then grant it to any user whose email appears in
// Admin:Emails (see appsettings.json). Runs on every startup and is idempotent (AddToRoleAsync on a
// user who already has the role is a safe no-op via Identity's own duplicate check), so redeploying
// doesn't create duplicate role assignments or throw if the list hasn't changed.
//
// A user must already exist for this to do anything - it promotes an existing account, it doesn't
// create one. The intended flow: register normally through the UI, add that email to Admin:Emails,
// restart the app once. Runs in its own scope (services registered at Scoped/Transient lifetime, like
// AppDbContext, aren't available on the root IServiceProvider `app.Services` directly).
using (var scope = app.Services.CreateScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roleManager.RoleExistsAsync("Admin"))
    {
        await roleManager.CreateAsync(new IdentityRole("Admin"));
    }

    var adminEmails = app.Configuration.GetSection("Admin:Emails").Get<string[]>() ?? Array.Empty<string>();
    if (adminEmails.Length > 0)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var email in adminEmails)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null && !await userManager.IsInRoleAsync(user, "Admin"))
            {
                await userManager.AddToRoleAsync(user, "Admin");
            }
        }
    }
}

// ---- LLM settings: load from database, if an admin has ever saved any ----
// LlmSettingsProvider (see AzureBuddy.Core/Llm/ILlmSettingsProvider.cs) already seeded itself from
// appsettings.json's Llm section when DI first constructed it. This overrides that with the database
// row's values, if one exists - the same "database wins over the config file, once someone has
// actually saved something" precedence LlmSettingsService.GetAsync uses when deciding what to show an
// admin. A no-op if no row exists yet (a brand new deployment), leaving the appsettings.json values in
// effect exactly as before this feature existed.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AzureBuddy.Core.Llm.LlmSettingsService>()
        .LoadFromDatabaseIfPresentAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Registered first (before routing/auth/anything else that could throw) so it wraps the entire
// request pipeline - any exception from any downstream middleware or controller gets caught here.
app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseRateLimiter();

// Must run before UseAuthentication/UseAuthorization - the browser's CORS preflight (OPTIONS)
// request carries no Authorization header, so if this ran later the preflight itself would get
// rejected by the auth pipeline before CORS ever got a chance to approve the real request.
app.UseCors();

// Authentication (who are you?) must run before Authorization (are you allowed?).
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Top-level statements generate an internal "Program" class by default. Making it public (partial,
// empty - it's purely a marker) is the standard ASP.NET Core pattern for letting integration tests
// use WebApplicationFactory<Program> to spin up this exact app in-memory with swapped-out config/DI
// (e.g. an in-memory database instead of real MySQL) - see tests/AzureBuddy.Tests/Integration/.
public partial class Program;
