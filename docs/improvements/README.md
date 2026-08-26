# AzureBuddy improvement audit

This directory holds a code audit of the AzureBuddy repository, split into seven standalone
workstream documents. Each file is self-contained — it names the project it touches, how to build
and test it, and every finding with a concrete `file:line` reference — so you can open any one of
them and start work without reading the others first.

## Files

| # | File | What it covers |
|---|---|---|
| 1 | [01-correctness-and-security.md](01-correctness-and-security.md) | Real bugs: dead account lockout, LLM timeout gaps, silent failures, an XSS-adjacent injection, secrets in logs. Small diffs, highest value. |
| 2 | [02-production-readiness.md](02-production-readiness.md) | First-deploy failures: no health checks, no migration step, startup crashes, CORS/config gaps. |
| 3 | [03-data-and-performance.md](03-data-and-performance.md) | EF Core query shape: missing `AsNoTracking`, missing `HasMaxLength`, missing indexes, N+1s. |
| 4 | [04-refactor-and-dedup.md](04-refactor-and-dedup.md) | Twelve duplicated code blocks and `Program.cs` decomposition. Behaviour-preserving. |
| 5 | [05-test-coverage.md](05-test-coverage.md) | Untested areas: the `/chat` endpoint, the whole agent namespace, both LLM provider clients, the frontend. |
| 6 | [06-build-and-ci.md](06-build-and-ci.md) | Missing analyzers, `strict` TypeScript, ESLint, and CI gaps (no test/lint step on the frontend job). |
| 7 | [07-frontend-a11y-ux.md](07-frontend-a11y-ux.md) | Chat UI accessibility and UX gaps: live regions, focus management, cancellation. |

## Suggested order

1. **01** — bugs first, small diffs, highest value.
2. **06** — guardrails (analyzers, CI, `strict`) before large-scale edits, so refactors are checked as they land.
3. **02** and **03** — deploy-blocking fixes and query-shape fixes, in either order.
4. **05** — tests, ideally written alongside 1–3 rather than after.
5. **04** — behaviour-preserving cleanup, safest once tests exist.
6. **07** — after 06's `strictTemplates` change lands, since both touch the same Angular templates.

The order is a suggestion, not a dependency — each document works standalone if you want to pick a
single one up in isolation.

## Where this audit came from

AzureBuddy is a .NET 8 + Angular 22 chatbot for Azure DevOps work-item management, ported from an
n8n workflow (`N8N chatbot files/`) and extended with multi-user Identity auth, per-user encrypted
Azure DevOps PATs, admin-owned LLM provider settings, and MySQL-backed chat history. The solution is
`AzureBuddy.slnx` with `src/AzureBuddy.Api` (controllers, `Program.cs`), `src/AzureBuddy.Core`
(business logic), `src/AzureBuddy.Data` (EF Core + MySQL via Pomelo), and `tests/AzureBuddy.Tests`
(xUnit + Testcontainers + WireMock). The Angular SPA lives in `web/`, talking to the API
cross-origin — there is no server-rendered UI.

This audit reflects the repository as of branch `bugfix/chat-fixes`, including one in-flight
uncommitted change that makes LLM provider settings purely admin-owned database state (removing the
`appsettings.json` binding). That change is coherent and referenced directly in
[01-correctness-and-security.md](01-correctness-and-security.md) where it interacts with a
DI-factory-throw bug.

Also worth knowing going in: `README.md`'s "Known shortcuts / deferred (v1)" section is stale — it
defers email verification and password reset, both of which now exist in code
(`AccountController`, `SmtpEmailSender`, `confirm-email`/`reset-password` pages). This `docs/`
directory was empty before this audit.

## A note not in any workstream doc

The untracked local `src/AzureBuddy.Api/appsettings.json` (correctly excluded by `.gitignore`, and
`git ls-files` confirms nothing has leaked into the repo) holds live plaintext secrets: a database
password, a JWT signing key, and a Gmail app password used for `Email:Smtp`. Nothing to fix in the
repo itself, but worth rotating that Gmail app password and moving local secrets to `dotnet user-secrets`
so they never risk landing in a commit.
