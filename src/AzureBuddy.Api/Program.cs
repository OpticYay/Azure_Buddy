using System.Text;
using System.Threading.RateLimiting;
using AzureBuddy.Api;
using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Auth;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Routing;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
builder.Services.AddIdentityCore<ApplicationUser>()
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(RateLimiterPolicies.Auth, limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

// Authentication (who are you?) must run before Authorization (are you allowed?).
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
