# Workstream 5 — Test coverage

## Context

The backend has 167 `[Fact]`/`[Theory]` tests across 20 files, with real infrastructure already
wired up: Testcontainers for a real MySQL database, WireMock.Net for mocking HTTP dependencies, and
a `FakeAdoClient`/`FakeEmailSender` pair for the integration tests. The frontend has Vitest
configured and ready. Despite that infrastructure, coverage has real gaps concentrated in exactly
the areas carrying the most logic — the conversational agent, both LLM provider clients, and the
`/chat` endpoint itself have zero tests. This workstream is cheap relative to its value because the
hard infrastructure work is already done; it's mostly a matter of writing the tests.

## Repo orientation

- Backend tests: `tests/AzureBuddy.Tests/`. Run with `dotnet test` (needs Docker for
  `Integration/` tests, which spin up real MySQL via Testcontainers). Unit tests (no `Integration/`
  prefix) don't need Docker.
- Key existing infra to reuse: `Integration/CustomWebApplicationFactory.cs` (spins up the full app
  with a per-test MySQL database and real migrations applied), `Integration/IntegrationTestBase.cs`,
  `Integration/SharedMySqlContainer.cs`, `AzureDevOps/FakeAdoClient.cs`,
  `Auth/FakeEmailSender.cs` (paths approximate — check `tests/AzureBuddy.Tests/` for exact
  locations). WireMock is already used in `AzureDevOps/AdoClientTests.cs` as the pattern to copy for
  mocking the Gemini/Ollama HTTP APIs.
- Frontend tests: `web/`, run with `npm test` (Vitest + jsdom, configured via
  `@angular/build:unit-test` in `web/angular.json`).
- Branch: `bugfix/chat-fixes`.

## Findings

### 5.1 `ChatController` / `POST /chat` has zero tests

`src/AzureBuddy.Api/Controllers/ChatController.cs` (106 lines) is the flagship endpoint — it's what
a user's actual chat message hits — and a search of `tests/` for `ChatController` or `"/chat"`
returns nothing.

**Add:** an integration test file (e.g. `Integration/ChatEndpointTests.cs`) using
`CustomWebApplicationFactory` + `FakeAdoClient`, covering: a message that resolves to a deterministic
flow (e.g. "show my items"), a message that goes to the conversational agent, the
`sessionId: null` create-a-session-as-a-side-effect path, the `AdoNotConfiguredException` → in-chat
error-message path (`ChatController.cs` ~lines 94–97), and the `NotFound` path for an unknown
session (~line 79 — also relevant to `04-refactor-and-dedup.md` §4.11's error-shape fix, so write
this test to check whatever shape that fix lands on).

### 5.2 The whole `Core/Agent/` namespace is untested

No test references `AzureBuddyAgent.cs` (168 lines), `AdoWorkItemToolset.cs` (323 lines — the
second-largest file in the repo), `ToolCatalog.cs` (146 lines), or `ChatHistoryStore.cs`.

**Add:**
- `AdoWorkItemToolsetTests` — unit tests against `FakeAdoClient` for each tool method (create bug,
  search, update state, get my items, get prioritized items), including the duplicated-logic paths
  flagged in `04-refactor-and-dedup.md` (§4.2, §4.3, §4.4, §4.6) — good candidates for
  characterization tests written *before* that refactor, so the refactor can be verified
  behavior-preserving against them.
- `AzureBuddyAgentTests` — mock `IChatCompletionClient` to test the tool-call loop, the
  `MaxToolCallRounds` limit, and the `ChatCompletionProviderException` → empty-string path flagged in
  `01-correctness-and-security.md` §1.3 (write this test to pin down whatever behavior that fix
  settles on).
- `ToolCatalogTests` — verify the tool schema JSON is well-formed and matches the tools actually
  registered.

### 5.3 `IntentExtractor` is untested

`src/AzureBuddy.Core/Intent/IntentExtractor.cs` (159 lines) routes every incoming message to one of
six intents and has no dedicated test file. **Add** `IntentExtractorTests` using a mocked/WireMock'd
LLM response covering: each of the six intents parsing correctly, malformed/unexpected JSON from the
model, and the provider-failure → `ChatIntent.Other` degradation path noted in
`01-correctness-and-security.md` §1.3.

### 5.4 LLM provider clients are untested

`src/AzureBuddy.Core/Llm/Providers/Gemini/GeminiChatClient.cs` (187 lines) and
`.../Ollama/OllamaChatClient.cs` (149 lines) have no tests — only the `FallbackChatClient` wrapper
around them is tested (`Llm/FallbackChatClientTests.cs`). WireMock.Net is already a project
dependency and already used for exactly this purpose against the Azure DevOps API
(`AzureDevOps/AdoClientTests.cs`), so the pattern to copy already exists in the repo.

**Add** `GeminiChatClientTests` / `OllamaChatClientTests` covering:
- A successful response parses correctly, including the "thought" part filtering in
  `GeminiChatClient.ParseResponse` (~lines 155–177 — the code that strips Gemini's internal reasoning
  parts from the final text, with a comment describing a real observed bug this fixes).
- A non-success HTTP status throws `ChatCompletionProviderException`.
- **Directly exercises `01-correctness-and-security.md` §1.2**: a WireMock stub that accepts the
  request promptly but delays the response body — assert the call is aborted at `TimeoutSeconds`
  once that fix lands (this test should fail against the current code, since the response-body read
  currently ignores the timeout-linked token).
- Tool-call round-tripping (`ToolCalls` in the request and response).

### 5.5 Three of five routing flows are untested

`Routing/Flows/MyItemsFlow.cs` and `UpdateItemFlow.cs` have tests
(`Routing/MyItemsFlowTests.cs`, `Routing/UpdateItemFlowTests.cs`); `CreateBugFlow.cs` (163 lines),
`ViewBugsFlow.cs` (64 lines), and `GetPrioritizedWorkItemsFlow.cs` (83 lines) do not. **Add**
`CreateBugFlowTests`, `ViewBugsFlowTests`, `GetPrioritizedWorkItemsFlowTests` following the existing
pattern in the two tested flows (against `FakeAdoClient`).

### 5.6 Encryption/protection paths tested only incidentally

`Settings/PatProtector.cs` and `Llm/LlmApiKeyProtector.cs` have no direct unit tests — they're only
exercised indirectly through endpoint tests that happen to save/load settings. **Add** direct
round-trip tests (protect then unprotect returns the original value) and a decryption-failure test
(garbage ciphertext raises `CryptographicException`, handled the way `MaskKey`/`MaskPat` expect —
relevant to `04-refactor-and-dedup.md` §4.7 if that consolidation happens first).

### 5.7 `WiqlQueryBuilder` escaping edge cases

`AzureDevOps/WiqlQueryBuilderTests.cs` already exists (16 tests) but per
`03-data-and-performance.md` §3.8, `Escape()` is the *sole* barrier against WIQL injection — worth
strengthening with explicit adversarial cases: a title containing a single quote, multiple quotes,
a quote immediately adjacent to other WIQL-significant characters, and confirming the escaped output
can't terminate the intended string literal early. Add these to the existing test file rather than a
new one.

### 5.8 `GlobalExceptionHandler` has no direct tests

No test constructs `src/AzureBuddy.Api/GlobalExceptionHandler.cs` directly and asserts its
`Classify` mapping. It's exercised indirectly wherever an integration test triggers a specific
exception, but there's no single test enumerating "each known exception type maps to the documented
status code and error code." **Add** a focused unit test (it only needs a fake `HttpContext`, no
`WebApplicationFactory`) enumerating every case in `Classify`, including whatever new case
`01-correctness-and-security.md` §1.3 adds for the LLM-not-configured scenario.

### 5.9 The frontend has effectively zero tests, and the one that exists cannot pass

`web/src/app/app.spec.ts` (17 lines) is the untouched Angular CLI scaffold. Its second test:

```ts
it('should render title', () => {
  // ...
  expect(compiled.querySelector('h1')?.textContent).toContain('Hello, web');
});
```

`web/src/app/app.html` is a single `<router-outlet></router-outlet>` — there is no `h1` anywhere in
the app shell, so this assertion fails (`textContent` on a missing element is `null`, which does not
`toContain` anything). This test is currently not run in CI (see `06-build-and-ci.md`), which is
presumably why this has gone unnoticed.

**Fix first, before adding anything else:** either delete this stale scaffold test or rewrite it to
assert something real about `AppComponent` (e.g. that it renders without throwing, or that the
router outlet is present).

**Then add**, in priority order:
- A Vitest spec for `markdown-lite.pipe.ts` (211 lines of regex-driven parsing feeding
  `bypassSecurityTrustHtml` — the highest-risk untested frontend file). Cover: bold/italic/code
  inline rendering, heading stripping, bullet lists, both GFM table forms (with and without outer
  pipes), the ID-column-to-link behavior, and — directly exercising
  `01-correctness-and-security.md` §1.4 — a `workItemBaseUrl` containing a `"` character, asserting
  the resulting HTML has no unescaped attribute-breakout.
- A spec for `core/services/message-classifier.ts` (32 lines) — the backend `type` →
  `DisplayMessage` union mapping; small, pure, and cheap to fully cover.
- A spec for `chat.service.ts` — the HTTP client wrapper — using Angular's `HttpClientTestingModule`
  to verify requests hit the expected endpoints with the expected payloads/headers, without a real
  backend.

## Verification

- `dotnet test` — all new and existing tests green. For the WireMock-based timeout test in §5.4,
  confirm it fails on the pre-fix code and passes once `01-correctness-and-security.md` §1.2 is
  applied, so it's proven to actually catch the bug it targets.
- `npm test` in `web/` — all new and existing tests green, including the fixed `app.spec.ts`.
- Once these pass locally, wire both commands into CI per `06-build-and-ci.md` so this coverage
  doesn't silently rot again the way `app.spec.ts` did.
