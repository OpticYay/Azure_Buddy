# Workstream 7 — Frontend accessibility & UX

## Context

The chat UI has some accessibility groundwork already in place (a labeled typing indicator,
`aria-hidden` on decorative icons, screen-reader-only labels on the attach button), but the core
conversational loop — the part a screen-reader user or keyboard-only user relies on most — has real
gaps: replies aren't announced, focus doesn't return to the input after sending, and one part of the
sidebar markup is invalid HTML. There's also no way to cancel a stuck send, which matters
functionally as well as for accessibility. Do this after `06-build-and-ci.md`'s `strictTemplates`
change lands, since both touch the same Angular templates and stacking them makes conflicts harder
to review.

## Repo orientation

- Chat feature: `web/src/app/features/chat/` — `chat-page/`, `session-list/`, `message-thread/`,
  `message-composer/`, `message-item/`. Shared markdown rendering:
  `web/src/app/shared/markdown-lite.pipe.ts`.
- Preview locally: `cd web && npm start` (Angular dev server), or use the project's browser-preview
  tooling to drive it interactively and inspect the accessibility tree.
- Test: `npm test` (Vitest). No component tests currently exist for this feature — see
  `05-test-coverage.md`.
- Branch: `bugfix/chat-fixes`. Land after `06-build-and-ci.md`'s TypeScript `strict`/`strictTemplates`
  change so template edits here are made under full type-checking from the start.

## Findings

### 7.1 The message log is not a live region

**File:** `message-thread.html` (~line 8) — a bare `<div class="log" #logContainer>` wrapping the
`@for` loop over `displayMessages()`. No `role="log"`, no `aria-live`, no `aria-label`. When a reply
arrives, a screen-reader user gets no announcement at all — they'd have to manually re-navigate into
the log to discover new content.

**Fix:** add `role="log"` and `aria-live="polite"` (not `"assertive"` — a chat reply isn't urgent
enough to interrupt whatever the user is currently doing) plus a descriptive `aria-label` such as
"Conversation".

### 7.2 No focus management after sending

**File:** `message-composer.ts` (~lines 230–272), the send path. The text is cleared but focus is
never explicitly returned to the textarea, and nothing moves focus to the newly-added message.
Focus handling exists elsewhere in the same file (starter-chip prefill, rename in `session-list.ts`
~lines 172–176 via `queueMicrotask(() => field.focus())`), so the pattern is established — it's just
not applied to the primary send path.

**Fix:** after a successful send, explicitly refocus the textarea (matching the existing
`queueMicrotask` pattern used for the starter chips) so keyboard users can continue typing without
having to tab back to the composer.

### 7.3 Loading and error states aren't announced

`message-thread.html` (~lines 10–12) renders "Loading conversation…" and any `loadError()` as a
plain `<p class="state">`. `message-composer.html` (~line 39) renders `errorMessage()` the same way.
Neither has `role="alert"` or `role="status"`, so a screen-reader user isn't told a send failed or a
conversation failed to load unless they happen to be focused there when it happens.

**Fix:** add `role="alert"` to the error paragraphs (errors are worth interrupting for) and
`role="status"` to the loading paragraph. Also add `aria-busy="true"` on the log container while
`loading()`/`isAwaitingReply()` is true, and on the composer while a send is in flight — currently
the only feedback is the Send button's label changing to "Sending", which isn't reliably exposed.

### 7.4 Invalid nested interactive elements in the session list

**File:** `session-list.html` (~lines 68–105). Two `<button>` elements (rename, delete) are nested
*inside* an `<a class="session" [routerLink]>`. Interactive elements nested inside other interactive
elements are invalid per the HTML spec — browsers and assistive technology handle the resulting
click/focus/tab semantics inconsistently. This is also *why* the component code needs
`stopPropagation()`/`preventDefault()` calls in `startRename` and `deleteSession` — those calls exist
specifically to work around the invalid nesting, and removing the nesting removes the need for them.

**Fix:** restructure so the rename/delete buttons are siblings of the link rather than children —
e.g. wrap the link and the action buttons in a common `<li>` or `<div class="session-row">`, with the
link covering the main clickable area and the buttons positioned alongside it (this is a common
pattern: `position: relative` on the row, buttons positioned absolutely or via flex layout, no
nesting required). This also lets the `stopPropagation()` workarounds in `startRename`/
`deleteSession` be removed, since there's no longer a parent link to accidentally trigger.

### 7.5 `title=` used as the only accessible label

**File:** `session-list.html`, rename and delete buttons — `title="..."` attributes only. A native
`title` tooltip is not reliably exposed to screen readers (support varies by browser/AT combination)
and is invisible to keyboard-only sighted users until they hover, which they can't do. **Fix:** add
`aria-label` (or visible, if small, text) to both buttons independent of `title`.

### 7.6 Unconditional scroll-to-bottom

**File:** `message-thread.ts` (~lines 210–232), `scrollToBottom()`. Implemented via `afterNextRender`
+ direct `scrollTop = scrollHeight`, with comments explaining this replaced an earlier
`queueMicrotask`/`scrollIntoView` approach that had its own issues — the current approach is
deliberate and well-reasoned for *triggering* the scroll correctly. The gap is that it fires
unconditionally on every load and every submit, regardless of whether the user has scrolled up to
read earlier history. A reply landing while someone is reading older messages yanks them back to the
bottom without warning. It also doesn't fire when the typing indicator appears/disappears, so the
one time a scroll might be most welcome (a reply just finished streaming in) is inconsistent with the
rest of the behavior.

**Fix:** track whether the user is currently near the bottom of the log (a scroll-position check)
before calling `scrollToBottom()` — only auto-scroll if they were already near the bottom. When
they've scrolled up and a new message arrives, show a "jump to latest" affordance instead of forcing
the scroll. This is a genuine UX design decision, not just a bug fix — confirm the intended behavior
before implementing if there's any doubt.

### 7.7 No cancellation anywhere

No `AbortController`, no `takeUntil`, no explicit `unsubscribe`, and no client-side timeout exist
anywhere in the frontend (confirmed by search). Sends are fire-and-forget RxJS subscriptions that
outlive session switches — the code compensates by guarding on a captured session id
(`message-thread.ts` ~line 174, `message-composer.ts` ~line 240) rather than actually cancelling the
in-flight request. There is no user-facing "stop generating" control, and a hung `POST /chat` leaves
the composer disabled indefinitely (backend timeout only, no frontend-side ceiling).

**Fix:** this is the largest item in this workstream — treat it as its own sub-task. At minimum, add
a client-side timeout on the chat HTTP call so a hung backend doesn't disable the composer forever,
and surface a "Stop" button during an in-flight send that aborts the underlying request (Angular's
`HttpClient` supports this via an `AbortSignal`-backed cancellation on the subscription — unsubscribe
from the `Observable` to cancel the underlying `XMLHttpRequest`/`fetch`).

### 7.8 Auth interceptor doesn't deduplicate concurrent refreshes

**File:** `web/src/app/core/interceptors/auth-interceptor.ts` (63 lines). On a 401, it refreshes the
access token and replays the request — correct for a single request, but if N requests get a 401
concurrently, each independently triggers its own `refreshAccessToken()` call, firing N refresh
requests against the backend instead of one shared one. **Fix:** share one in-flight refresh
`Observable` (e.g. via a `shareReplay(1)`-backed subject reset after completion) so concurrent 401s
all wait on the same refresh call.

### 7.9 Smaller items

- **Textarea doesn't auto-grow** — `message-composer.html` (~lines 51–60) is `rows="1"` with no
  resize logic; a long multi-line draft scrolls inside one row instead of growing. Add auto-resize
  (a small `(input)` handler adjusting `scrollHeight`, or a CDK `cdkTextareaAutosize` directive if
  Angular CDK is an acceptable new dependency).
- **Generic screenshot `alt` text** — every attached screenshot gets
  `alt="Screenshot attached as evidence"` (`message-item.html` ~line 111), and the composer's own
  preview thumbnail uses `alt=""` (`message-composer.html` ~line 7). Not fixable to be fully
  descriptive automatically, but at minimum make the composer's preview `alt` non-empty (e.g.
  "Attached screenshot preview").
- **`window.confirm()` for delete** (`session-list.ts` ~line 216) — a native browser dialog,
  inconsistent with the app's own toast/notification system used everywhere else. Replace with an
  in-app confirmation (a toast with an undo action, or a small modal) matching the existing design
  language.
- **Duplicated `.sr-only` utility class** — defined separately in `session-list.css` (~line 310) and
  `message-composer.css` (~line 209). Move to a shared stylesheet (`web/src/styles.css` or a small
  shared partial) so future screen-reader-only styling doesn't need a third copy.
- **Production API URL is still the placeholder** — `web/src/environments/environment.ts` points at
  `https://your-deployed-api.example.com`. Not an accessibility issue, but worth fixing alongside
  this pass since it's in the same area of the codebase and is a one-line change once the real
  deployment URL is known.

## Verification

- **Keyboard-only pass:** tab through send, rename, and delete without a mouse; confirm every action
  is reachable and that focus lands somewhere sensible after each (particularly after send, per §7.2,
  and after the session-list restructure, per §7.4).
- **Screen-reader spot check:** send a message and confirm the reply is announced (§7.1), trigger a
  send failure and confirm it's announced as an alert (§7.3).
- **Browser preview tooling:** use `preview_start` against the `web` dev server, then `read_page` to
  inspect the accessibility tree directly for `role`/`aria-*` attributes rather than relying on visual
  inspection alone; `resize_window` to confirm nothing here regresses at mobile widths.
- **Manual scroll test (§7.6):** scroll up mid-conversation, trigger a new reply, confirm you're not
  yanked back to the bottom.
- **Manual cancellation test (§7.7):** simulate a slow/hung backend (e.g. a WireMock delay in a local
  test setup, or throttling in devtools) and confirm the new Stop control actually aborts the
  request rather than just hiding the UI state.
