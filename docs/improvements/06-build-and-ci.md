# Workstream 6 — Build & CI hardening

## Context

None of the tooling that would normally catch regressions automatically is switched on: no shared
analyzer configuration for the .NET projects, no TypeScript `strict` mode, no ESLint, and a CI
pipeline whose frontend job only builds — it never runs the frontend's own test suite, never lints,
and never checks formatting. This workstream is guardrail work: it doesn't fix any specific bug, but
it's what stops the bugs in the other workstreams' fixes (and future changes) from silently
regressing. Doing this before large-scale changes (workstream 4's refactor in particular) means
those changes are checked as they land rather than after the fact.

## Repo orientation

- Backend: `AzureBuddy.slnx` at the repo root, four projects under `src/` and `tests/`, all
  targeting `net8.0`. No `Directory.Build.props`, no root `.editorconfig`, no `global.json` exist
  today — confirmed by directory listing.
- Frontend: `web/`, Angular 22 (standalone components, zoneless), TypeScript ~6.0.2, built with
  `@angular/build:application` (esbuild). Test runner is Vitest via `@angular/build:unit-test`.
  `web/.prettierrc` and `web/.editorconfig` exist; no ESLint config exists anywhere.
- CI: `.github/workflows/ci.yml`, two jobs (`backend`, `frontend`), triggered on push/PR to `main`
  only.
- Branch: `bugfix/chat-fixes` — note this branch itself gets **no CI run** today, since the workflow
  only triggers on `main`.

## Findings

### 6.1 No shared build configuration for the .NET projects

No `Directory.Build.props`, no root `.editorconfig`, no `global.json`. Consequences:

- `Nullable` and `ImplicitUsings` are set to `enable` identically in all four `.csproj` files
  (consistent today, but nothing enforces staying that way as projects are added).
- No `TreatWarningsAsErrors`, no `EnableNETAnalyzers`, no `AnalysisLevel`, no
  `EnforceCodeStyleInBuild` anywhere — the built-in .NET analyzers and code-style rules exist but
  aren't switched on, so nullable-reference warnings and style violations can accumulate silently.
- No `global.json` pinning the SDK version. CI's `setup-dotnet@v4` step uses `dotnet-version:
  "10.0.x"` (needed because `.slnx` — the newer XML solution format — requires a newer SDK to parse,
  even though every project still targets `net8.0`), but nothing in the repo enforces that locally;
  a contributor with only the .NET 8 SDK installed can't build the solution file at all without
  discovering this by trial and error.

**Fix:**
1. Add a root `Directory.Build.props` hoisting the currently-duplicated `<Nullable>`/
   `<ImplicitUsings>` settings, plus `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`,
   `<EnableNETAnalyzers>true</EnableNETAnalyzers>`, `<AnalysisLevel>latest</AnalysisLevel>`, and
   `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`. Expect this to surface existing
   warnings — budget time to fix them or add targeted suppressions before merging.
2. Add a root `.editorconfig` (`root = true`, C# formatting/analyzer severities) — `web/.editorconfig`
   exists today but is frontend-only.
3. Add `global.json` pinning the SDK version actually used in CI, so `dotnet build` behaves
   identically locally and in CI.

### 6.2 TypeScript `strict` mode is off

`web/tsconfig.json` sets `noImplicitOverride`, `noPropertyAccessFromIndexSignature`,
`noImplicitReturns`, `noFallthroughCasesInSwitch`, `isolatedModules`, and `skipLibCheck` — a
deliberate-looking selection, but `"strict": true` is conspicuously absent, which is a departure from
the Angular CLI's default `ng new` output. Concretely, `strictNullChecks` and `noImplicitAny` are
both off. `angularCompilerOptions` sets `strictInjectionParameters` and `strictInputAccessModifiers`
but not `strictTemplates` — meaning template expressions (like the non-null assertion at
`message-item.html` ~line 110, `adoAttachmentUrl!`) get less type checking than the rest of the
codebase's careful typing would suggest.

**Fix:** add `"strict": true` to `web/tsconfig.json` and `"strictTemplates": true` to
`angularCompilerOptions`. Expect a real round of fixes — `strictNullChecks` alone typically surfaces
dozens of previously-silent null/undefined issues in a 7,400-line codebase. Budget this as its own
PR, separate from any feature work, and do it **before** `07-frontend-a11y-ux.md`'s template changes
land, since both touch the same files and stacking them makes conflicts and review harder.

### 6.3 No ESLint at all

No `.eslintrc*` or `eslint.config.*` exists anywhere in `web/`, no `@angular-eslint` dependency, and
no `lint` script in `package.json`. Prettier **is** installed (`web/.prettierrc`: printWidth 100,
singleQuote, angular parser for HTML) but there is no `format`/`format:check` script invoking it
anywhere, so nothing currently enforces it — spot checks find files exceeding the configured
printWidth (e.g. long lines in `message-thread.ts`, `message-composer.ts`, `message-item.html`),
consistent with Prettier not being run consistently today.

**Fix:**
1. Add `@angular-eslint` + `typescript-eslint` and a flat `eslint.config.js` matching the project's
   standalone/signals style.
2. Add `"lint": "eslint ."` and `"format:check": "prettier --check ."` /
   `"format": "prettier --write ."` scripts to `web/package.json`.
3. Wire both into CI (see §6.4).

### 6.4 CI gaps

**File:** `.github/workflows/ci.yml`.

- **Triggers on `main` only** — pushes and PRs to any other branch (including this one,
  `bugfix/chat-fixes`) get no CI feedback at all. Add `pull_request` targeting all branches, or at
  minimum widen the branch filter.
- **Frontend job runs only `npm ci && npm run build`.** No `npm test`, no lint, no format check —
  meaning the broken `app.spec.ts` (see `05-test-coverage.md` §5.9) has never actually been run in
  CI, which is presumably why it's been broken without anyone noticing. Add `npm test`, and once
  §6.3's scripts exist, `npm run lint` and `npm run format:check`.
- **Backend job has no coverage collection/upload** despite `coverlet.collector` already being a
  test-project dependency — the investment is there but unused. Add
  `dotnet test --collect:"XPlat Code Coverage"` and upload the result (to the workflow's artifact
  storage at minimum; a coverage-tracking service is a further option but not required).
- **No `dotnet format --verify-no-changes` step** — nothing currently blocks a PR with inconsistent
  C# formatting.
- **No NuGet package caching** — every CI run does a full restore from scratch; add
  `actions/setup-dotnet`'s built-in caching or `actions/cache` keyed on the lock file.
- **Stale comment:** lines ~61–63 explain why frontend tests aren't run, referencing Karma/Chrome —
  but the project has already moved to Vitest (`@angular/build:unit-test` in `angular.json`,
  `vitest`/`jsdom` in `package.json`). Once `npm test` is added per above, this comment (and the
  reasoning it documents) is obsolete — delete it rather than update it.

### 6.5 No `.gitattributes`

The in-flight uncommitted change on this branch (`ILlmSettingsProvider.cs`, `LlmSettingsService.cs`)
already shows CRLF/LF line-ending warnings in its diff — evidence this is a live problem, not a
hypothetical one. Add a root `.gitattributes` normalizing line endings (e.g. `* text=auto eol=lf` for
source files), so contributors on different platforms don't generate spurious whitespace-only diffs.

### 6.6 No Dependabot, no CodeQL, no `.nvmrc`

Lower priority than the above, but worth tracking: no `.github/dependabot.yml` for either NuGet or
npm, no CodeQL (or equivalent) security-scanning workflow, and no `.nvmrc` pinning the Node version
locally (CI pins Node 22 via `actions/setup-node`, but a local contributor has no equivalent pin to
follow). Add as follow-ups once the higher-priority items above are in place.

## Verification

- After §6.1: `dotnet build` should now surface (or fail on, once `TreatWarningsAsErrors` is on) any
  existing nullable/analyzer warnings — resolve or explicitly suppress each before merging.
- After §6.2: `cd web && npx tsc --noEmit` should report the new strict-mode errors; work through
  them file by file rather than mass-suppressing.
- After §6.3: `npm run lint` and `npm run format:check` should both pass on a clean tree; deliberately
  introduce a lint violation and a formatting violation locally to confirm each check actually fails.
- After §6.4: push a commit to a non-`main` branch and confirm CI now runs; deliberately break a
  frontend test locally, confirm `npm test` fails, then confirm CI catches it too.
- After §6.5: introduce a file with mixed line endings and confirm `git diff` no longer shows the
  whole file as changed after a normalize/renormalize pass.
