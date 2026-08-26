# Workstream 3 — Data & performance

## Context

EF Core query patterns across the codebase have a consistent shape: no read-only queries use
`AsNoTracking()`, no custom entity string column has an explicit `HasMaxLength` (so everything is
MySQL `longtext`, including a two-value enum), and the indexes that exist don't match the queries
actually being run. None of this is broken today — the app works — but it's the kind of debt that
turns into real latency once session/message volume grows. Each item below is independent.

## Repo orientation

- Data layer: `src/AzureBuddy.Data` — `AppDbContext.cs` (`IdentityDbContext<ApplicationUser>`, 6
  `DbSet`s), entities, and `Migrations/`. No repository layer — `src/AzureBuddy.Core` services talk
  to `AppDbContext` directly (`ChatSessionService`, `AuthService`, `AccountService`,
  `UserAdoConfigService`, `LlmSettingsService`, `WorkItemStateConfigService`).
- Build/test: `dotnet build` / `dotnet test` from the repo root. Integration tests use real MySQL via
  Testcontainers (`tests/AzureBuddy.Tests/Integration/`), so you can observe actual query plans, not
  just LINQ shape.
- To see generated SQL locally, add `.LogTo(Console.WriteLine, LogLevel.Information)` to the
  `AddDbContext<AppDbContext>` options in `Program.cs` temporarily (revert before committing).
- Branch: `bugfix/chat-fixes`.

## Findings

### 3.1 No `AsNoTracking()` on any read-only query

A repo-wide search for `AsNoTracking` in `src/` returns zero matches. Every read in the app —
including ones that only ever display data and never modify it — pays EF Core's change-tracking
overhead unnecessarily. Concretely:

- `src/AzureBuddy.Core/Chat/ChatSessionService.cs`, `ListSessionsAsync` (~lines 52–61) and
  `GetSessionAsync` (~lines 68–70, which also `.Include`s the full message collection — see §3.4).
- `src/AzureBuddy.Core/WorkItemStates/WorkItemStateConfigService.cs` (~lines 24–27, 39–44).
- `src/AzureBuddy.Core/Settings/UserAdoConfigService.cs` (~lines 126–127).
- `src/AzureBuddy.Core/Llm/LlmSettingsService.cs` (~lines 120–121).
- `src/AzureBuddy.Core/Account/AccountService.cs` (~lines 156–158).

**Fix:** add `.AsNoTracking()` to each of these. Leave tracking on wherever the same query result is
later mutated and saved in the same unit of work (verify each site individually — don't blanket-add
without checking).

### 3.2 No `HasMaxLength` on any custom entity — everything is `longtext`

`AppDbContext.cs` and the entity classes define string properties with no length constraint, so
every custom column in MySQL ends up as `longtext` rather than a bounded `varchar`. This is visible
directly in the migrations, e.g. `Migrations/20260802035024_AddRolesSupportAndLlmSettings.cs`
(~lines 19–31) for `LlmSettings.ProvidersCsv/GeminiModel/GeminiBaseUrl/GeminiEncryptedApiKey/
OllamaModel/OllamaBaseUrl`, and the original `ChatSessions`/`ChatMessages` blocks in
`20260731211847_InitialCreate.cs`.

The clearest case: `ChatMessage.Role` is a two-value enum persisted via `HasConversion<string>()`
(`AppDbContext.cs`, ~lines 57–59) — and is stored as `longtext` because nothing bounds it. A column
that can only ever hold `"User"` or `"Assistant"` doesn't need unbounded storage, and `longtext`
columns can't be part of an efficient index the same way a bounded `varchar` can (relevant directly
to §3.3 below, since `ChatMessages.Role` and similar columns interact with query planning).

**Fix:** add `HasMaxLength(...)` in `AppDbContext`'s `OnModelCreating` for:
- `ChatSessions.Title`
- `ChatMessages.Role`, `ChatMessages.AdoAttachmentUrl`
- `UserAdoSettings.OrganizationUrl`, `.DefaultProject`, `.EncryptedPat`
- `LlmSettings.ProvidersCsv`, `.GeminiModel`, `.GeminiBaseUrl`, `.GeminiEncryptedApiKey`,
  `.OllamaModel`, `.OllamaBaseUrl`

Leave `ChatMessages.Content` and `.TableDataJson` as unbounded `longtext` — those legitimately need
to hold large free-form content. Generate one migration for this change; review the generated SQL
carefully since a `longtext` → `varchar(n)` change on MySQL can truncate existing data if any
current value exceeds `n` — pick lengths generously (e.g. 512 for URLs, matching what the app itself
already validates on the way in) and consider a data-length audit query before applying to a
populated database.

### 3.3 Indexes don't match the queries actually run

Current indexes (from the migrations): `UserAdoSettings.UserId` (unique), `ChatSessions.UserId`,
`ChatMessages.SessionId`, `RefreshTokens.TokenHash`, `RefreshTokens.UserId`,
`WorkItemStateConfigurations.WorkItemType` plus a unique `(WorkItemType, StateName)`.

- `ChatSessionService.ListSessionsAsync` (~lines 52–61) filters by `UserId` and orders by
  `UpdatedAt`. The existing index covers only `UserId`, so MySQL must filesort every page. Add a
  composite index on `ChatSessions (UserId, UpdatedAt)`.
- `ChatSessionService.GetSessionAsync` (~lines 68–70) orders included messages by `CreatedAt`; add
  `ChatMessages (SessionId, CreatedAt)`.
- `AccountService.cs` (~lines 156–175, see §3.5) filters active refresh tokens per user; add
  `RefreshTokens (UserId, RevokedAt)`.

**Fix:** add these three composite indexes via `OnModelCreating` + one migration (can be combined
with the §3.2 migration).

### 3.4 Unbounded message loading in `GetSessionAsync`

**File:** `src/AzureBuddy.Core/Chat/ChatSessionService.cs`, `GetSessionAsync` (~lines 68–70):

```csharp
.Include(s => s.Messages.OrderBy(m => m.CreatedAt))
```

No message count limit and no `AsSplitQuery()`. A long-running conversation returns every message
row in one query, each carrying a `longtext` `Content` and `TableDataJson` column — for both the
initial page load and (per `ListSessionsAsync`'s correlated `EXISTS` subquery, see §3.6) every list
refresh that checks whether a session has any messages.

**Fix:** either cap the number of messages returned (with a "load earlier messages" pattern on the
frontend) or add `.AsSplitQuery()` if the full history genuinely needs to load at once — decide based
on how the frontend's `message-thread.ts` actually consumes this today before choosing.

### 3.5 Loop-based operations that should be set-based

- `ChatSessionService.DeleteEmptySessionsAsync` (~lines 121–132) materializes matching sessions into
  memory, then calls `RemoveRange`. Replace with `ExecuteDeleteAsync()` — no need to load entities
  just to delete them.
- `AccountService.cs` (~lines 156–175) loads all of a user's active refresh tokens into memory, then
  revokes them in a `foreach` loop (one row assignment per iteration, one `SaveChangesAsync` for the
  batch). Replace with `ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow))`
  filtered the same way. Note: if workstream 1's refresh-token-family-revocation fix (§1.6 in
  `01-correctness-and-security.md`) is implemented, that new code path is a natural place to apply
  this fix too, since it does the same kind of bulk revoke.

### 3.6 Redundant round trips

- `ChatSessionService.ListSessionsAsync` (~lines 52–61) does `.Where(s => s.Messages.Any())` (a
  correlated `EXISTS` subquery evaluated per row) plus a separate `CountAsync`, then a paged
  `ToListAsync` — two round trips per list request, each re-evaluating the subquery over the user's
  full session set.
- `AdoWorkItemToolset.UpdateWorkItemAsync` (~line 186) and `UpdateItemFlow.ValidateStateAsync`
  (~line 88) each issue an extra Azure DevOps API call solely to read `System.WorkItemType` before
  every state update — a network round trip to Azure DevOps, not just a DB query, so this one has a
  much higher latency cost than the others in this section.
- `AdoAttachmentService.cs` (~lines 71–123) issues three sequential PATCH calls to Azure DevOps per
  screenshot upload (fetch work item → embed in description → add evidence comment).
- `WorkItemStateConfigService.GetEnabledStateNamesAsync` is called on every single work-item state
  update (both call sites in the previous bullet call into it) but its result — the set of valid
  state names for a work item type — changes only when an admin edits the config. The
  `Microsoft.Extensions.Caching.Memory` package is already referenced in `AzureBuddy.Core.csproj`
  but `IMemoryCache` is never used anywhere in the codebase (confirmed by search) — this is the
  obvious place to start using it, with a short TTL or explicit invalidation on
  `WorkItemStateConfigService`'s write path.

**Fix:** the `WorkItemStateConfigService` caching is the highest-value, lowest-risk item here — do
that first. The ADO round-trip reductions and the `ListSessionsAsync` two-query shape are worth
doing but touch more surface area; treat them as follow-ups once the cache is in.

### 3.7 An empty migration

`src/AzureBuddy.Data/Migrations/20260801132026_InitialAuthAdoChatSchema.cs` has an empty `Up()` and
`Down()` (both just `{ }`), yet carries a 460-line `.Designer.cs` snapshot file alongside it — dead
weight in the migration history.

**Fix:** only remove this if you confirm no deployed/shared database has this migration recorded in
its `__EFMigrationsHistory` table — removing a migration that's already been applied somewhere
breaks that database's ability to apply future migrations cleanly. If any doubt exists, leave it and
just note it as intentionally empty.

### 3.8 `WiqlQueryBuilder`'s injection barrier is a single hand-rolled escape

**File:** `src/AzureBuddy.Core/AzureDevOps/WiqlQueryBuilder.cs`. Azure DevOps's WIQL query language
has no parameterization API — the only defense against injection is `Escape()` (~line 83), which
doubles single quotes. This is used consistently across `SearchByTitle` (~line 21),
`SearchByTitleWords` (~line 33), `StateFilter` (~line 76), and `WorkItemTypeFilter` (~line 79).
`ChildrenOf(int parentId)` (~lines 57–58) interpolates an `int` directly, which is safe by type
(can't contain a quote). This is not a code defect — the escaping is applied everywhere it needs to
be — but it is the *only* barrier, so it deserves explicit test coverage rather than living as an
implicit assumption. See `05-test-coverage.md` for the specific test to add.

## Verification

- Enable `.LogTo(Console.WriteLine, LogLevel.Information)` on the `AppDbContext` registration in a
  local run, hit the session list endpoint, and confirm the generated SQL uses the new
  `(UserId, UpdatedAt)` index (visible via `EXPLAIN` on the emitted query, or by confirming no
  `Using filesort` in MySQL's query plan).
- Run the full integration suite (`dotnet test`) after the `HasMaxLength` migration — the existing
  `AccountEndpointsTests`, `SettingsEndpointsTests`, and `LlmSettingsEndpointsTests` exercise these
  columns and will catch any accidental truncation of test fixture data.
- For §3.5's `ExecuteDeleteAsync`/`ExecuteUpdateAsync` changes, confirm the existing tests for
  `DeleteEmptySessionsAsync` and token revocation still pass — behavior should be identical, only the
  generated SQL changes shape.
