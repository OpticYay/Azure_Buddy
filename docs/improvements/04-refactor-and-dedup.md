# Workstream 4 — Refactor & dedup

## Context

Twelve blocks of duplicated logic were found across the backend and frontend, plus one oversized
`Program.cs` and one error-response inconsistency. None of these are bugs today, but each
duplication is a place where a future fix will be applied to one copy and forgotten in the other —
several pairs already show signs of drifting apart. This workstream is meant to be
behaviour-preserving: run the existing test suite before and after each change and expect it to stay
green throughout. Safest to do once the test gaps in `05-test-coverage.md` are filled, so any
accidental behavior change gets caught.

## Repo orientation

- Backend logic lives in `src/AzureBuddy.Core`, namespaces `Agent/`, `Routing/Flows/`,
  `AzureDevOps/`, `Llm/`, `Settings/`, `Intent/`. API layer in `src/AzureBuddy.Api/Controllers` and
  `src/AzureBuddy.Api/Program.cs`.
- Frontend chat components in `web/src/app/features/chat/`, shared markdown rendering in
  `web/src/app/shared/markdown-lite.pipe.ts`.
- Build/test: `dotnet build && dotnet test` for the backend, `cd web && npm ci && npm test` for the
  frontend. Run both before starting and after every change in this workstream.
- Branch: `bugfix/chat-fixes`.

## Findings

### 4.1 Work-item table rendering duplicated three times

`src/AzureBuddy.Core/Routing/Flows/MyItemsFlow.cs` (~lines 12–16, 45, 53–57),
`ViewBugsFlow.cs` (~lines 12–16, 52, 59–63), and `GetPrioritizedWorkItemsFlow.cs`
(~lines 16–20, 50, 58–62) each independently declare the same `Fields` array, the same table
headers, and the same `BuildRow` logic for turning a work item into a markdown table row.

Also note: `AdoFields.DueDateCandidates` (`src/AzureBuddy.Core/AzureDevOps/AdoModels.cs`, ~line 114)
already exists as a shared field-name constant and is never referenced — each flow re-declares its
own field array instead of using it.

**Fix:** extract a single `WorkItemTableBuilder` (or similar) that all three flows call. Use
`AdoFields.DueDateCandidates` from it rather than leaving it dead.

### 4.2 `MyItemsFlow` and `GetPrioritizedWorkItemsFlow` are near-identical, duplicated twice over

`MyItemsFlow` and `GetPrioritizedWorkItemsFlow` (both in `Routing/Flows/`) use the same
`AssignedToMe` WIQL query (`MyItemsFlow.cs:31-34` ≡ `GetPrioritizedWorkItemsFlow.cs:35-38`), the
same fetch, the same `SortByUrgency` call, and the same table build — differing only in the summary
line shown to the user.

The same pair is duplicated **again**, independently, inside the agent's tool implementation:
`src/AzureBuddy.Core/Agent/AdoWorkItemToolset.cs`, `GetMyWorkItemsAsync` (~lines 227–242) vs.
`GetPrioritizedWorkItemsAsync` (~lines 248–264) — byte-identical except for one `SortByUrgency` call.

**Fix:** extract the shared "fetch my assigned items, optionally sort by urgency" logic into one
method used by both flows and both toolset methods, parameterized by whether urgency sorting is
applied and by the summary text. This removes four near-duplicate call sites down to one shared
implementation plus four thin callers.

### 4.3 Work-item-state validation implemented twice

`Routing/Flows/UpdateItemFlow.cs`, `ValidateStateAsync` (~lines 85–108), and
`Agent/AdoWorkItemToolset.cs` (~lines 184–210, inline) both do the same three steps: fetch the work
item's type, call `WorkItemStateConfigService.GetEnabledStateNamesAsync`, then case-insensitively
match the requested state — differing only in how the result is formatted for their respective
callers.

**Fix:** extract a shared `WorkItemStateValidator` returning a result type both call sites can adapt
to their own output format.

### 4.4 Two-stage title search duplicated

`Agent/AdoWorkItemToolset.cs` (~lines 54–63) and `Routing/Flows/CreateBugFlow.cs` (~lines 35–53)
both implement: try `SearchByTitle` first, and if that returns nothing, fall back to
`SearchByTitleWords`. Extract one shared method (e.g. on `AdoClient` or a small helper) that both
call.

### 4.5 Bug-description HTML template duplicated in three places that must stay byte-compatible

- `Routing/Flows/CreateBugFlow.cs` (~lines 155–161) — the C# string building the description HTML.
- `Agent/AzureBuddyAgent.cs` (~line 47) — the same template repeated as an instruction in the
  system prompt (so the LLM produces matching HTML when it builds a bug description itself).
- `AzureDevOps/AdoAttachmentService.cs` (~line 7) — `EvidencePlaceholder = "<b>Evidence:</b> Not
  provided"`, which **parses** text produced by the other two, so all three must agree on the exact
  string.

This is the riskiest duplication in the list because the three copies aren't just similar code —
they're a producer/producer/consumer triangle that silently breaks if any one drifts. **Fix:**
define the template (and the placeholder string) once, as a constant referenced by all three: the
flow uses it directly, the system-prompt text is generated from it (or at minimum a code comment in
`AzureBuddyAgent.cs` points at the constant so a future edit updates both), and
`AdoAttachmentService` parses against the same constant rather than a separately-typed literal.

### 4.6 Bug-creation JSON-patch assembly duplicated

`Agent/AdoWorkItemToolset.cs`, `CreateLinkedBugAsync` (~lines 98–109), and
`Routing/Flows/CreateBugFlow.cs`, `CreateBugAsync` (~lines 120–134) build the same JSON-patch
operations list and the same parent-link URL
(`{orgUrl}/{project}/_apis/wit/workItems/{parentId}`, appearing at `AdoWorkItemToolset.cs:102` and
`CreateBugFlow.cs:124`), including the same optional-field handling. Extract a shared
`BugCreationRequestBuilder` (or extend `AdoClient` with a purpose-built method) used by both.

### 4.7 Credential masking and protection wrappers duplicated

- `Llm/LlmSettingsService.cs`, `MaskKey` (~lines 180–188), and
  `Settings/UserAdoConfigService.cs`, `MaskPat` (~lines 146–158) are near-identical:
  `new string('•', 8) + lastFour`, with the same `try/catch (CryptographicException)` fallback in
  both. Extract one shared `SecretMasking.Mask(string)` helper.
- `Llm/LlmApiKeyProtector.cs` and `Settings/PatProtector.cs` are structurally identical Data
  Protection wrappers, differing only in the purpose string passed to
  `IDataProtectionProvider.CreateProtector(...)`. Collapse to one generic
  `NamedSecretProtector`/`ProtectorFactory` taking the purpose string as a constructor parameter, and
  have both current classes become thin named instances (or DI registrations) of it.

### 4.8 Intent taxonomy declared in four places

The six chat intents are declared independently in four spots: `Intent/ChatIntent.cs` (the enum
itself), the JSON schema string in `IntentExtractor.cs` (~line 24), the parsing switch in
`IntentExtractor.ParseIntent` (~lines 125–133), and the routing switch in `IntentRouter.cs`
(~lines 60–68). Adding a seventh intent today means four separate edits, with no compiler error if
one is missed (the JSON schema string and the parsing switch are both just strings/switches, not
tied to the enum by the type system).

**Fix:** at minimum, make `ParseIntent`'s switch exhaustive over the enum (so the compiler flags a
missing case when a new value is added — use a `switch` expression without a default arm, or add an
`_ => throw` that's easy to notice). Consider generating the JSON schema's intent list from the enum
via reflection or a source generator so it can't silently drift from the type. `IntentRouter`'s
switch will still need a manual case per intent (that's inherent to routing), but making the other
three sites derive from the enum reduces four maintenance points to two.

### 4.9 Frontend table rendering duplicated

`web/src/app/features/chat/message-item/message-item.html` (~lines 68–103, for structured tables
from deterministic flows) and `web/src/app/shared/markdown-lite.pipe.ts` (~lines 140–175, for
markdown tables the agent writes as prose) each independently implement "if this is the ID column,
render a linked stamp" — `message-item.ts`'s `cellWorkItemUrl`/`isIdColumn` (~lines 67–76) vs. the
pipe's `renderCell` and its own `idColumn` lookup. The pipe's own comment (~line 145) already
acknowledges this is a copy of the other implementation's rule.

**Fix:** extract the shared "is this the ID column, and if so build a linked stamp for this value"
logic into one function both the pipe and the template's supporting TypeScript call. Since one side
is a template-driven component and the other is a pipe producing raw HTML, the cleanest shared unit
is likely a plain function (not a component) that both import.

### 4.10 `Program.cs` is doing more than composition

**File:** `src/AzureBuddy.Api/Program.cs`, 313 lines. Roughly 180 lines are DI/middleware
configuration and roughly 45 lines are imperative startup logic (the admin-role seeding and
LLM-settings-loading blocks covered in `02-production-readiness.md` §2.3). Nine `Add*Xxx()` extension
methods already exist and establish the pattern this file should fully follow
(`AddLlmProviders`, `AddAzureDevOps`, `AddWorkItemStates`, `AddChatRouting`, `AddAzureBuddyAgent`,
`AddAzureBuddyAuth`, `AddAzureBuddyAccount`, `AddAdoSettings`, `AddChatHistory`).

**Fix:** extract the remaining inline blocks — CORS setup, Swagger setup, JWT bearer setup, the rate
limiter, and (per `02-production-readiness.md` §2.3) the seeding logic — into their own
`Add*`/`Use*` extension methods under a new `src/AzureBuddy.Api/Extensions/` folder, following the
existing naming convention. This is purely mechanical (move code, no logic change) and should not
alter behavior — verify with the integration test suite, which exercises the fully composed app via
`WebApplicationFactory<Program>`.

### 4.11 Inconsistent error response shapes

`GlobalExceptionHandler`'s own doc comment states its purpose is giving "every unhandled exception
the same `ApiErrorResponse` JSON body regardless of where it was thrown" — but several controllers
bypass it by returning error responses directly, in three different shapes:

- `ChatController.cs` (~line 79): `return NotFound("Chat session not found.")` — a bare string body.
- `ChatsController.cs` (~lines 51, 73, 80, 127, 174): `NotFound()` with an empty body.
- `WorkItemStatesAdminController.cs` (~lines 43, 50): `NotFound()` with an empty body.
- vs. `ChatsController.cs` (~lines 123, 132, 140) and `LlmSettingsController.cs` (~line 52), which
  correctly construct an `ApiErrorResponse`.

**Fix:** standardize every explicit error return on `ApiErrorResponse`, matching what
`GlobalExceptionHandler` already produces for unhandled exceptions, so the frontend's error-parsing
code (`web/src/app/core/models/api-error.model.ts`) has exactly one shape to handle regardless of
whether the error came from a thrown exception or an explicit controller return.

### 4.12 Dead code and a package version mismatch

- `Api/Models/ChatModels.cs` (~line 37): `AppendMessageRequest` is unused —
  `ChatsController.AppendMessageAsync` takes individual `[FromForm]` parameters instead. Delete it,
  or wire the controller to use it (pick one; don't leave both).
- `Microsoft.EntityFrameworkCore.InMemory` is referenced in `tests/AzureBuddy.Tests.csproj` but the
  suite uses real MySQL via Testcontainers for integration tests; verify no unit test still depends
  on the InMemory provider, then drop the package reference if genuinely unused.
- `Microsoft.Extensions.Caching.Memory` is referenced in `AzureBuddy.Core.csproj` but `IMemoryCache`
  is never instantiated anywhere (see `03-data-and-performance.md` §3.6, which proposes actually
  using it) — resolve by either adopting it there or removing the reference, not by leaving it as an
  unused dependency.
- `AzureBuddy.Core.csproj` (~lines 11–14) pins `Microsoft.Extensions.Caching.Memory`,
  `.Http`, `.Http.Polly`, and `.Options` at **10.0.10**, while the project targets `net8.0` and every
  other package in the solution is on the 8.0.x line. This is very likely an accidental version bump
  rather than an intentional one. Align these four to the matching 8.0.x versions unless there's a
  specific reason (documented) for the mismatch.

## Verification

Run `dotnet test` and `npm test` before starting and after each individual change in this
workstream — every item here is meant to be behaviour-preserving except §4.11 (the error-shape
standardization), which is a deliberate, visible change to response bodies for the endpoints listed;
check whether any existing test asserts the old bare/empty-body shape and update those assertions
alongside the fix.
