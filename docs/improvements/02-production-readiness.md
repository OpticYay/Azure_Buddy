# Workstream 2 — Production readiness

## Context

These issues don't show up in local development at all — they only surface on first real deploy:
a dead health endpoint that doesn't exist, a schema that was never migrated, a startup that crashes
when the database isn't reachable yet, and a CORS policy that silently keeps running with
`localhost` origins in production. Each is independent; fix in any order.

## Repo orientation

- Solution: `AzureBuddy.slnx` — `src/AzureBuddy.Api` (ASP.NET Core 8, all wiring in
  `src/AzureBuddy.Api/Program.cs`, 313 lines of top-level statements), `src/AzureBuddy.Core`,
  `src/AzureBuddy.Data` (EF Core + Pomelo MySQL), `tests/AzureBuddy.Tests`.
- Build/run: `dotnet build`, then `dotnet run --project src/AzureBuddy.Api`. Requires a MySQL
  connection string at `ConnectionStrings:Default` and `Jwt:SigningKey` — see
  `src/AzureBuddy.Api/appsettings.Example.json` for the full shape (copy it to
  `appsettings.Development.json`, which is gitignored).
- Test: `dotnet test` (needs Docker for the Testcontainers-based integration tests).
- Branch: `bugfix/chat-fixes`.

## Findings

### 2.1 No health checks

Nothing in the codebase calls `AddHealthChecks()` or `MapHealthChecks()` — confirmed by a
repo-wide search. There is no way for a load balancer, container orchestrator, or uptime monitor to
ask "is this instance alive and able to reach its database?" without hitting an authenticated
business endpoint.

Note the global auth default: `Program.cs` (~lines 179–187) sets
```csharp
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
```
Every endpoint requires auth unless explicitly opted out — so a health endpoint must carry
`[AllowAnonymous]` (the only other place that does today is `AuthController`).

**Fix:** add `builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>()` (or an explicit
MySQL check) and `app.MapHealthChecks("/health/live")` / `MapHealthChecks("/health/ready")` with
`.AllowAnonymous()`, near the other `app.Map*` calls at the bottom of `Program.cs`.

### 2.2 No `Database.Migrate()` anywhere in the running app

A repo-wide search for `Database.Migrate` finds exactly one call:
`tests/AzureBuddy.Tests/Integration/CustomWebApplicationFactory.cs` (~line 47), used only to prepare
a fresh per-test database. Nothing in `src/AzureBuddy.Api/Program.cs` applies migrations — the
production app assumes the schema already exists by the time it starts.

**Fix:** decide deliberately between (a) calling `dbContext.Database.Migrate()` in a startup scope
in `Program.cs`, guarded so it only runs when explicitly enabled (an env var or config flag, since
auto-migrating on every instance start is dangerous in a multi-instance deployment), or (b)
documenting a required `dotnet ef database update` step in the deploy process. Either is fine; the
current state — neither happens, and it isn't written down anywhere — is the problem. Whatever is
chosen, add it to `README.md`'s deployment section.

### 2.3 Startup admin-role seeding can crash the app on boot

**File:** `src/AzureBuddy.Api/Program.cs`, ~lines 247–268.

```csharp
using (var scope = app.Services.CreateScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roleManager.RoleExistsAsync("Admin"))
    {
        await roleManager.CreateAsync(new IdentityRole("Admin"));
    }
    // ... FindByEmailAsync / AddToRoleAsync loop over Admin:Emails ...
}
```

This runs before `app.Run()`, with no try/catch and no retry. If MySQL is unreachable at the moment
the container starts (a common race in orchestrated deployments where the DB and app start
together), every one of these calls throws, the exception propagates out of the top-level
`Program.cs` script, and the process exits — `app.Run()` never executes, so even the health endpoint
from §2.1 never comes up to report the problem clearly. The same applies to the second startup
block immediately after it (~lines 275–279), which calls
`LlmSettingsService.LoadFromDatabaseIfPresentAsync()`.

**Fix:** wrap both startup scopes in a retry (a handful of attempts with backoff is enough — this
only needs to survive a normal container-orchestration race, not a sustained outage) and log clearly
on each failed attempt before giving up. While doing this, extract the ~35-line seeding block into
its own class (e.g. `AdminRoleSeeder`) — `Program.cs` is already long, and this is imperative
business logic sitting in what should be pure composition-root wiring. Pass a real
`CancellationToken` to the Identity calls in both blocks instead of the implicit `default` they use
today.

### 2.4 CORS silently falls back to localhost origins in production

**File:** `src/AzureBuddy.Api/Program.cs`, ~lines 59–70.

```csharp
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200", "http://127.0.0.1:4200" };
```

This fallback is a reasonable convenience for local development, but `Cors:AllowedOrigins` is **not
present in `src/AzureBuddy.Api/appsettings.Example.json`** — so a new deployer following the example
file, or a deployer who simply forgets this key, ends up running production with a CORS policy that
only allows `localhost:4200`. Combined with `.AllowCredentials()` on the same policy (needed for the
`Authorization` header, per the comment at lines 63–65), a misconfigured production deployment
doesn't throw or warn — it just makes the API unreachable from the real frontend origin, which is a
confusing failure to debug from the browser's console alone.

Same gap for `Admin:Emails` (used at `Program.cs:255`, in the seeding block from §2.3) — also
missing from the example file, so a new deployer has no way to discover it exists.

**Fix:**
1. Add both `Cors:AllowedOrigins` and `Admin:Emails` to `appsettings.Example.json` with placeholder
   values and a comment.
2. In non-Development environments, fail fast at startup (throw, the same pattern already used for
   `ConnectionStrings:Default` at line 131 and `Jwt:SigningKey` at line 159) if
   `Cors:AllowedOrigins` is absent, instead of silently falling back to localhost.

### 2.5 No configuration validation

No options class in the app uses `ValidateOnStart()` / `ValidateDataAnnotations()`. The only
config-presence checks are the two manual `?? throw` guards already mentioned (connection string,
JWT signing key) — everything else (`SmtpOptions`, `AdoOptions`, `AppOptions`) is bound silently and
only fails, if at all, the first time a code path actually tries to use a missing or malformed
value.

One concrete case: `Program.cs:158-159` checks that `Jwt:SigningKey` exists, but not that it's long
enough. `SymmetricSecurityKey` has a minimum key size for the signing algorithm in use; a
short key doesn't fail at startup — it throws the first time a token is minted, deep inside the JWT
library, which is a much harder failure to diagnose in production than a clear startup error.

**Fix:** register the bound-options classes with `.ValidateDataAnnotations().ValidateOnStart()` and
add `[MinLength(32)]` (or whatever the algorithm requires) to the signing-key property, or add an
explicit length check next to the existing null check at line 158.

### 2.6 `UseHttpsRedirection` without `UseHsts`

**File:** `src/AzureBuddy.Api/Program.cs`, ~line 292. HTTPS redirection is configured but HSTS
(`app.UseHsts()`) is not, so browsers aren't told to remember to always use HTTPS for this origin —
the standard defense-in-depth pairing is incomplete. Add `app.UseHsts()` for non-Development
environments (it's normally skipped in Development to avoid caching issues with self-signed certs).

### 2.7 Rate limiting only protects `AuthController`

**File:** `src/AzureBuddy.Api/Program.cs`, ~lines 199–209, and `RateLimiterPolicies.cs`.

The only rate-limiter policy defined is `RateLimiterPolicies.Auth`, applied to `AuthController` (5
requests/minute per IP). `ChatController`'s `/chat` endpoint — which on every call makes at least one
LLM request and potentially several Azure DevOps API calls — has no rate limiting at all. This is
both a cost-control gap (LLM API calls are billed) and an abuse-protection gap.

**Fix:** add a second named policy (e.g. a per-user, not per-IP, fixed or sliding window — the user
is authenticated by the time this endpoint is reached, so a per-user key is both more precise and
harder to route around than per-IP) and apply it to `ChatController`.

### 2.8 Unbounded in-memory chat history

**File:** `src/AzureBuddy.Core/Agent/ChatHistoryStore.cs` (~line 12), registered as `Singleton` in
`AgentServiceCollectionExtensions.cs`.

```csharp
private readonly ConcurrentDictionary<string, ChatHistory> _histories = new();
```

Every session's in-memory agent history (distinct from the persisted `ChatMessages` table — this is
specifically the working memory the agent reads to maintain tool-call context within a conversation)
stays resident in this dictionary for the lifetime of the process, with no eviction, no TTL, and no
size cap. A long-running instance accumulates memory proportional to the total number of distinct
sessions ever touched, not the number of *active* sessions. The class's own comment documents that
this state isn't shared across instances (a separate, accepted limitation for horizontal scaling),
but says nothing about unbounded growth on a single instance.

**Fix:** the project already references `Microsoft.Extensions.Caching.Memory` (currently unused —
see workstream 4) — replace the raw dictionary with `IMemoryCache` configured with a size limit and
sliding expiration, so idle sessions are evicted automatically.

### 2.9 Data Protection keys on local filesystem

**File:** `src/AzureBuddy.Api/Program.cs`, ~lines 211–222. The code and its own comment already
correctly document the risk:

> By default, Data Protection keys live under the OS user profile, which breaks the moment this app
> runs in a container or scales to more than one instance... in a real multi-instance deployment, a
> *shared* one (mounted volume, or `PersistKeysToDbContext`/Azure Blob storage instead of the
> filesystem)...

This is flagged here only to make sure it's tracked as an action item rather than staying a comment
someone has to stumble onto. If a multi-instance or containerized deployment is planned, this needs
to move to a shared store before that happens — otherwise every user's encrypted PAT and LLM API key
becomes unrecoverable garbage the moment a second instance (with its own generated key) starts
decrypting rows the first instance encrypted.

### 2.10 `ChatController` route inconsistency

**File:** `src/AzureBuddy.Api/Controllers/ChatController.cs` — routed at `chat`, while all seven
other controllers use an `api/` prefix (`api/auth`, `api/chats`, `api/settings/ado`, etc.). Cosmetic
on its own, but worth fixing for consistency — note it's a breaking API change for any client, so
coordinate with `web/src/app/core/services/chat.service.ts`, which currently calls `/chat` directly.

## Verification

- **2.1:** `curl -i http://localhost:5013/health/live` and `/health/ready` with no `Authorization`
  header — both should return 200 without the global auth fallback rejecting them.
- **2.2:** document or implement the migration step, then verify a truly empty MySQL database ends
  up with the full schema after following that step (or after normal startup, if auto-migrate was
  chosen).
- **2.3:** stop MySQL, start the API, confirm the process logs a clear retry/failure message instead
  of crashing silently, and that `/health/live` (once implemented) still responds even if
  `/health/ready` reports unhealthy.
- **2.4:** start the API in `Production` with `Cors:AllowedOrigins` absent from config — confirm
  startup now fails loudly rather than silently defaulting to localhost.
- **2.5:** start the API with a 16-byte `Jwt:SigningKey` — confirm it fails at startup, not at first
  login.
- **2.7:** hammer `/chat` past the new limit and confirm a 429, matching the existing behavior
  already verified for `/api/auth/*`.
