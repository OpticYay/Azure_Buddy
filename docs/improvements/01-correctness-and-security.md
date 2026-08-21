# Workstream 1 — Correctness & security bugs

## Context

These are real defects — several have code comments that assert the *opposite* of what the code
does, which is exactly the kind of bug that survives review. Each fix here is small and independent
of the other workstreams. This is the highest-value tranche in the audit: start here.

## Repo orientation

- Solution: `AzureBuddy.slnx` — `src/AzureBuddy.Api` (ASP.NET Core 8, controller-based),
  `src/AzureBuddy.Core` (business logic, no framework dependency beyond DI abstractions),
  `src/AzureBuddy.Data` (EF Core + Pomelo MySQL), `tests/AzureBuddy.Tests` (xUnit + Testcontainers
  MySQL + WireMock.Net). Frontend: Angular 22 SPA in `web/`, talking to the API cross-origin.
- Build/test backend: `dotnet build` / `dotnet test` from the repo root (Testcontainers needs Docker
  running for the integration tests in `tests/AzureBuddy.Tests/Integration/`).
- Build/test frontend: `cd web && npm ci && npm test` (Vitest).
- Branch: `bugfix/chat-fixes`. There is one relevant in-flight uncommitted change — see the note at
  the end of this doc under §1.3.

## Findings

### 1.1 Account lockout never engages

**File:** `src/AzureBuddy.Core/Auth/AuthService.cs`, method `LoginAsync` (around line 204).

```csharp
var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
if (!passwordValid)
{
    _logger.LogWarning("Failed login attempt for {Email}.", request.Email);
    return AuthResult.Fail(invalidCredentials);
}

if (await _userManager.IsLockedOutAsync(user))
{
    return AuthResult.Fail(new ApiError("account_locked", "..."));
}
```

The comment directly above this (lines 219–220) says:

> `CheckPasswordAsync` (via `PasswordHasher`) does a constant-time comparison of the hash, and
> `UserManager` tracks failed attempts toward the lockout policy configured in `Program.cs`.

That second half is false. `UserManager.CheckPasswordAsync` verifies a password hash and returns a
bool — it does **not** touch `AccessFailedCount`. Only `SignInManager.CheckPasswordSignInAsync(user,
password, lockoutOnFailure: true)`, or an explicit call to `UserManager.AccessFailedAsync(user)`,
increments the failure counter that lockout depends on. Neither `SignInManager` nor
`AccessFailedAsync` appears anywhere in `src/` (confirmed by repo-wide search) — `Program.cs:138`
even has a comment explaining that `AddIdentityCore` (not `AddIdentity`) was chosen specifically
*because* `SignInManager` isn't needed for a JWT-only API, without registering a replacement for the
lockout bookkeping it normally provides.

**Consequence:** `AccessFailedCount` is always 0, so `IsLockedOutAsync` at line 228 can never return
true. The `account_locked` branch is dead code, and the `Identity:Lockout:MaxFailedAccessAttempts: 5`
/ `DefaultLockoutTimeSpan` values in `appsettings.json` do nothing. The only brute-force defence that
actually works today is the per-IP fixed-window rate limiter on `AuthController`
(`Program.cs:199-209`, 5 requests/minute) — which is per-IP, not per-account, so a distributed
attempt against one account from many IPs is not slowed down at all.

**Fix:**
1. Call `await _userManager.AccessFailedAsync(user)` when the password check fails, and
   `await _userManager.ResetAccessFailedCountAsync(user)` on a successful login.
2. Move the `IsLockedOutAsync` check **before** the password check (or at minimum before returning
   success) — as currently ordered, a request that arrives with the correct password while the
   account happens to be locked would fall through the `!passwordValid` branch and get tokens
   issued, once counting starts working.
3. Correct the comment at lines 219–220 to state what actually happens after the fix.
4. Leave the identical-failure-message design (`invalidCredentials` used for both "no such user" and
   "wrong password", documented at lines 208–211) exactly as it is — that anti-enumeration reasoning
   is sound and orthogonal to this bug.

### 1.2 LLM provider timeout doesn't cover reading the response body

**Files:** `src/AzureBuddy.Core/Llm/Providers/Gemini/GeminiChatClient.cs` (lines 40–61),
`src/AzureBuddy.Core/Llm/Providers/Ollama/OllamaChatClient.cs` (same shape).

```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

HttpResponseMessage response;
try
{
    response = await _httpClient.PostAsJsonAsync(url, requestBody, cts.Token);   // line 46: cts.Token ✓
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    throw new ChatCompletionProviderException(ProviderName, "Gemini request failed or timed out.", ex);
}

if (!response.IsSuccessStatusCode)
{
    var body = await response.Content.ReadAsStringAsync(cancellationToken);      // line 55: outer token ✗
    ...
}

var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken); // line 60: outer token ✗
```

Only the initial POST is bound to the timeout-linked `cts.Token`. Reading the response body — the
error-body read at line 55 and the success-body read at line 60 — uses the caller's outer
`cancellationToken`, which has no relationship to `TimeoutSeconds`. A provider that accepts the
request headers promptly but then stalls while streaming the body back can hang indefinitely past
the configured timeout, defeating the whole point of `TimeoutSeconds` (which the class comment at
`LlmServiceCollectionExtensions.cs:37-44` explains was made hot-reloadable and made the sole timeout
authority precisely so an admin-configured value is honored).

**Fix:** pass `cts.Token` to both response-body reads in both clients, not `cancellationToken`.

While in these files: `response` (the `HttpResponseMessage` from `PostAsJsonAsync`) is never
disposed in either client — wrap it in `using`. The same undisposed-response pattern exists in
`src/AzureBuddy.Core/AzureDevOps/AdoClient.cs`, method `SendAsync` (~lines 149–163); neither of its
callers (`ReadOrThrowAsync` ~line 172, `TestConnectionAsync` ~line 131) disposes the response either.

### 1.3 LLM provider failures surface as a friendly HTTP 200, not the intended error

**Files:** `src/AzureBuddy.Core/Agent/AzureBuddyAgent.cs` (~lines 112–120),
`src/AzureBuddy.Core/Routing/IntentRouter.cs` (`EnsureNonEmpty`, ~lines 102–114),
`src/AzureBuddy.Api/GlobalExceptionHandler.cs` (line 57),
`src/AzureBuddy.Core/Llm/LlmServiceCollectionExtensions.cs` (lines 57–61).

`GlobalExceptionHandler.Classify` has a specific, deliberate mapping:

```csharp
ChatCompletionProviderException => (StatusCodes.Status502BadGateway,
    new ApiError("llm_provider_unavailable", "All configured AI providers failed to respond. Please try again shortly.")),
```

But that mapping is unreachable from the agent conversation path. `AzureBuddyAgent` catches the
exception itself:

```csharp
catch (ChatCompletionProviderException ex)
{
    _logger.LogError(ex, "All LLM providers failed while responding to session {SessionId}.", sessionId);
    return string.Empty;
}
```

The empty string then flows to `IntentRouter.EnsureNonEmpty`, which turns any blank/failed reply
into a normal `ChatReply` with `Type = Error` and a friendly message — returned as an ordinary
**HTTP 200**. A client (or a monitoring system) watching for 502s to detect provider outages will
never see one from a conversational turn. `Core/Intent/IntentExtractor.cs` (~lines 69–73) has the
same pattern: a provider failure there silently degrades to `ChatIntent.Other` rather than
propagating.

This is a *design* choice that makes sense for the chat UX (never show the user a raw error) but
currently also erases the signal `GlobalExceptionHandler` was built to surface. Decide deliberately:
either let `ChatCompletionProviderException` propagate out of the agent so the existing 502 mapping
actually fires (and have the frontend / `ChatController` translate that into the same friendly
in-chat message it shows today), or keep the swallow but log at a severity that feeds alerting, and
document in the `EnsureNonEmpty` comment that the 502 path is intentionally never hit from here.

**Related, separate bug in the same area:** `LlmServiceCollectionExtensions.cs:57-61` throws
`InvalidOperationException` (not `ChatCompletionProviderException`) from inside a **DI factory** when
no provider is configured:

```csharp
if (options.Providers.Count == 0)
{
    throw new InvalidOperationException(
        "No LLM provider is configured - an admin must set one up from the LLM settings screen (/admin/llm) before chat will work.");
}
```

`GlobalExceptionHandler.Classify` has no case for `InvalidOperationException`, so it falls into the
generic 500 `unexpected_error` — the actionable "go configure it at /admin/llm" message written into
the exception is discarded and never reaches the client or even a structured log field (only the
generic "Unhandled exception" log line at `GlobalExceptionHandler.cs:40` captures it, as full
exception text).

This matters more than it might otherwise: there is an in-flight uncommitted change on this branch
(`src/AzureBuddy.Api/Program.cs`, `appsettings.Example.json`, and the four `Llm/*` files) that makes
LLM settings purely admin-owned database state, with no `appsettings.json` fallback. That change is
coherent on its own — but it means **every fresh deployment** now starts in the
"`options.Providers.Count == 0`" state described above until an admin visits `/admin/llm`, so this
becomes the default first-run experience, not an edge case.

**Fix:** introduce a dedicated exception (e.g. `LlmNotConfiguredException`) thrown from the DI
factory instead of `InvalidOperationException`, and add a `GlobalExceptionHandler.Classify` case
mapping it to 503 with the existing actionable message intact.

### 1.4 Markdown pipe can inject through an unescaped attribute value

**File:** `web/src/app/shared/markdown-lite.pipe.ts`, function `renderCell` (~line 169) and
`escapeHtml` (~line 206).

```ts
function renderCell(cell: string, isIdColumn: boolean, workItemBaseUrl: string | null): string {
  const id = cell.trim();
  if (!isIdColumn || !workItemBaseUrl || !/^\d+$/.test(id)) {
    return renderInline(cell);
  }
  return `<a class="stamp" href="${workItemBaseUrl}/${id}" target="_blank" rel="noopener">#${id}</a>`;
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
```

The resulting HTML string is passed to `this.sanitizer.bypassSecurityTrustHtml(...)` in `transform`
(line 39) — Angular's DOM sanitizer is deliberately disabled for this output, on the stated
assumption (the pipe's own doc comment, lines 21–24) that "the input is LLM output... treated as
untrusted the same way user input would be" and "only the tags this pipe itself builds ever reach
`bypassSecurityTrustHtml`, never anything from the source text directly."

That assumption doesn't fully hold for `workItemBaseUrl`. It is not LLM output — it comes from
`message-thread.ts` (~lines 122–127), sourced from the current user's own saved Azure DevOps
`organizationUrl` setting (a free-text `type="url"` input in `ado-settings.html`). `id` is safely
guarded by `/^\d+$/`, but `workItemBaseUrl` is interpolated into the `href` attribute with **no
escaping at all**, and `escapeHtml` — which runs earlier over the raw markdown text, not over
`workItemBaseUrl` — never escapes `"` or `'` even where it does apply. A saved `organizationUrl`
containing a `"` breaks out of the `href="..."` attribute inside HTML that bypasses sanitization
entirely.

**Fix:**
1. Add `"` (and ideally `'`) to `escapeHtml`'s replacements.
2. At the `renderCell` interpolation site, HTML-escape `workItemBaseUrl` (or URL-encode it) before
   building the attribute — don't rely solely on the general `escapeHtml` pass, since that runs over
   the markdown body, not over this separately-sourced value.
3. As defence in depth, add a stricter format check to `SaveAdoSettingsRequest` server-side (it
   already likely has `[Url]` — verify it rejects values containing quote characters, or add an
   explicit check) so a malformed `organizationUrl` can't be saved in the first place.

### 1.5 Secrets and sensitive data reach the logs

- **`src/AzureBuddy.Core/Auth/IEmailSender.cs`, `NoOpEmailSender.SendAsync` (~lines 30–36):**
  ```csharp
  _logger.LogInformation(
      "No email provider configured (see Email:Smtp:Host) - would have sent to {ToEmail}, subject {Subject}:\n{Body}",
      toEmail, subject, body);
  ```
  This logs the **entire email body at Information level**, including password-reset links and
  email-confirmation tokens — anyone with read access to application logs can complete either flow
  for any user. This is not a rare fallback: it's the default registration whenever
  `Email:Smtp:Host` is blank (`AuthServiceCollectionExtensions.cs`, ~lines 22–30), which is likely
  true in every local/dev environment. Fix: log only `toEmail` and `subject`; if the body needs to
  be inspectable for local development, log it at `Debug` with an explicit comment that this must
  never be enabled where logs are shared or persisted.

- **`src/AzureBuddy.Core/AzureDevOps/AdoClient.cs` (~lines 180–181):** logs the full ADO response
  body, and the same body is placed into the exception message that
  `GlobalExceptionHandler.cs:56` returns to the client verbatim as `ado_api_error`. ADO error
  bodies can include project/org metadata. Truncate or redact before logging and before including in
  the client-facing message.

- **`GeminiChatClient.cs` / `OllamaChatClient.cs` (~line 56 in both):** log the full provider
  response body at Warning on any non-success status. Same truncation/redaction concern.

- **`GeminiChatClient.cs` (line 38):** the API key is placed in the request **URL query string**:
  ```csharp
  var url = $"{_options.BaseUrl}/models/{_options.Model}:generateContent?key={_options.ApiKey}";
  ```
  This means the key lands in `response.RequestMessage.RequestUri`, which is reachable from any
  handler-level or proxy-level logging that captures request URIs (including the response-body
  logging above, if it were ever extended to log the request too). Gemini's API accepts the key via
  the `x-goog-api-key` header — move it there instead of the query string.

### 1.6 No refresh-token family revocation on replay

**File:** `src/AzureBuddy.Core/Auth/AuthService.cs`, `RefreshAsync` (~lines 238–259).

Refresh tokens rotate correctly on every use (the presented token is revoked, a new one issued), and
a revoked/expired/unknown token is rejected uniformly via `stored.IsActive`. This correctly blocks a
*simple* replay of an already-used token. What's missing: if a revoked token is presented (as
opposed to simply "not found"), that specifically indicates token *reuse* — the strongest signal
available that a refresh token has been stolen and used by both the legitimate holder and an
attacker. The current code treats that the same as any other invalid token and stops there, rather
than treating it as a compromise signal.

**Fix:** when the presented token's hash matches a stored row that is already revoked (not merely
missing or expired), treat it as reuse: revoke every other active refresh token for that user (the
whole "family"), forcing re-authentication everywhere. Log this case distinctly from an ordinary
invalid/expired token, since it's the one case worth alerting on.

## Verification

- **1.1:** integration test in `AuthEndpointsTests` — 5 failed logins followed by a correct-password
  attempt should return `account_locked`, not tokens. Also verify a successful login after some
  failures resets the counter (a 6th attempt with the right password after 4 wrong ones should
  succeed, not lock).
- **1.2:** a WireMock-based test (the pattern already used in `AdoClientTests.cs`) where the mocked
  provider accepts the request immediately but delays the body indefinitely; assert the call
  completes (fails) at `TimeoutSeconds`, not later.
- **1.3:** a test asserting a simulated provider-down scenario returns a distinguishable outcome
  (502 if you choose to propagate, or an explicitly logged/metrics-visible event if you keep the
  swallow) rather than an indistinguishable 200.
- **1.4:** a Vitest unit test for `markdownLite` with `workItemBaseUrl` containing a `"` character;
  assert the resulting HTML has no unescaped quote breaking out of the `href` attribute.
- **1.5:** grep the log output of a local run (with `Email:Smtp:Host` unset) for the string "reset"
  or a known token fragment — it should not appear at Information level after the fix.
- **1.6:** integration test: obtain a refresh token, use it (rotates, old one revoked), then replay
  the *original* (now-revoked) token; assert all other active tokens for that user are also revoked
  after this call.
