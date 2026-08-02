import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, finalize, map, of, shareReplay, tap, throwError } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  AuthTokens,
  LoginRequest,
  RegisterRequest,
} from '../models/auth.models';

const REFRESH_TOKEN_STORAGE_KEY = 'azurebuddy_refresh_token';

/** Decoded from the access token's claims (see TokenService.CreateAccessToken on the backend) - the
 * backend never sends a separate "user profile" object back, so this is all we know client-side.
 * `roles` mirrors the JWT's "role" claim(s) - empty for an ordinary user, e.g. `['Admin']` for
 * someone promoted via the Admin:Emails startup seeding (see Program.cs). */
export interface CurrentUser {
  id: string;
  email: string;
  roles: string[];
}

// ── What is a "service" in Angular, and why is this decorated with @Injectable? ────────────────────
// A service is just a plain TypeScript class that holds logic/state you want to SHARE across multiple
// components, instead of each component reimplementing it (or worse, duplicating "who's logged in"
// state in five different places that could disagree with each other). `@Injectable({ providedIn: 'root' })`
// registers this class with Angular's dependency-injection system (introduced in app.config.ts) as a
// single, app-wide instance ("singleton") - every component or other service that asks for an
// AuthService via its constructor gets the exact same instance, so they all see the same login state.
@Injectable({ providedIn: 'root' })
export class AuthService {
  // ── What is a signal? ──────────────────────────────────────────────────────────────────────────
  // A signal is a container around a value that Angular can watch. When you call `.set(...)` on one,
  // Angular knows *exactly* which parts of the UI read that signal (via `.set()` calls being tracked)
  // and automatically re-renders only those parts - you never manually push a DOM update yourself the
  // way older frameworks required. Contrast this with a plain TypeScript variable: if you did
  // `let accessToken = null` and later reassigned it, nothing watching that variable would know it
  // changed, and the screen simply wouldn't update.
  //
  // This one holds the current JWT access token in memory ONLY (see the class-level comment below for
  // why not localStorage). `signal<string | null>(null)` means "a signal whose value is either a
  // string or null, starting out null" - TypeScript's `|` here is a union type, "one of these types."
  private readonly accessToken = signal<string | null>(null);

  // A "computed" signal derives its value from other signals and automatically recalculates whenever
  // any signal it reads from changes - you never manually keep it in sync. Here: whenever accessToken
  // changes, currentUser recomputes by decoding the new token's claims (or becomes null if logged out).
  // `computed()` producing a *readonly* signal (no `.set()` on it) is intentional - the only way to
  // change who's "current user" is by changing accessToken, not by setting currentUser directly.
  readonly currentUser = computed<CurrentUser | null>(() => {
    const token = this.accessToken();
    return token ? decodeAccessTokenClaims(token) : null;
  });

  readonly isAuthenticated = computed(() => this.currentUser() !== null);

  /** Read by adminGuard and AppShell's nav to show/gate the LLM settings screen. Derives from
   * currentUser() the same way isAuthenticated does - there is no separate "am I admin" API call,
   * the answer already arrived encoded in the JWT the moment the user logged in. */
  readonly isAdmin = computed(() => this.currentUser()?.roles.includes('Admin') ?? false);

  // Multiple HTTP requests can 401 around the same moment (e.g. a page that fires 3 API calls on
  // load, all with an expired token). Without this, each one would independently kick off its own
  // POST /api/auth/refresh - wasteful, and could even race and invalidate each other's new tokens.
  // This field remembers an in-flight refresh call so every 401 can share the SAME one; see
  // refreshAccessToken() below for how it's used, and the auth interceptor for who calls it.
  private refreshInFlight$: Observable<AuthTokens> | null = null;

  constructor(private readonly http: HttpClient) {}

  // ── Why does an HTTP call return an Observable instead of a Promise? ───────────────────────────
  // A Promise represents ONE eventual value and starts running the moment it's created. An Observable
  // is more like a subscription: nothing happens until something calls `.subscribe(...)` on it (this
  // is called being "cold" - the HTTP request literally isn't sent until you subscribe), and it can
  // represent zero, one, or many values over time, plus be cancelled mid-flight (e.g. if a user
  // navigates away before a request finishes, Angular's router can unsubscribe and abort it - a
  // Promise can never be cancelled once started). Angular's HttpClient uses Observables for exactly
  // this cancellability, and because `.pipe(...)` lets you compose operators (retry, map, catchError,
  // combine-with-another-request, etc.) declaratively, which is awkward with Promises/async-await.
  // Signals, by contrast, are for SYNCHRONOUS state you read at any time (no "subscribe" needed) -
  // that's why the token itself is a signal, but the network call to fetch a new one is an Observable.
  register(request: RegisterRequest): Observable<AuthTokens> {
    return this.http
      .post<AuthTokens>(`${environment.apiUrl}/api/auth/register`, request)
      .pipe(tap((tokens) => this.applySession(tokens)));
  }

  login(request: LoginRequest): Observable<AuthTokens> {
    return this.http
      .post<AuthTokens>(`${environment.apiUrl}/api/auth/login`, request)
      .pipe(tap((tokens) => this.applySession(tokens)));
  }

  /** Called once at app startup (see app.config.ts's provideAppInitializer) to turn a still-valid
   * refresh token sitting in localStorage back into a fresh access token, so refreshing the browser
   * tab doesn't force a re-login. Never throws - if there's no stored token, or the refresh fails
   * (expired/revoked), we just end up logged out, which is the correct fallback either way. */
  tryRestoreSession(): Observable<void> {
    const storedRefreshToken = localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    if (!storedRefreshToken) {
      return of(undefined);
    }

    return this.refreshAccessToken().pipe(
      // `map` transforms each emitted value - here we don't care about the tokens themselves (that's
      // already been handled by refreshAccessToken's own `tap`), just that the call finished, so we
      // map the result to `undefined` to match this method's `Observable<void>` return type.
      map(() => undefined),
      // Swallow any error too - a failed silent refresh should look like "not logged in," not crash
      // app boot.
      catchError(() => of(undefined)),
    );
  }

  refreshAccessToken(): Observable<AuthTokens> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const storedRefreshToken = localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    if (!storedRefreshToken) {
      return throwError(() => new Error('No refresh token available.'));
    }

    // `shareReplay(1)` is what makes the de-duplication described above work: normally each new
    // `.subscribe()` on an Observable re-runs everything from scratch (a second HTTP call). shareReplay
    // instead runs the source ONCE and replays that same result to every subscriber. `finalize` clears
    // refreshInFlight$ once the call settles (success or failure) so the *next* 401, later, correctly
    // starts a new refresh instead of replaying a stale one forever.
    this.refreshInFlight$ = this.http
      .post<AuthTokens>(`${environment.apiUrl}/api/auth/refresh`, { refreshToken: storedRefreshToken })
      .pipe(
        tap((tokens) => this.applySession(tokens)),
        catchError((err) => {
          this.clearSession();
          return throwError(() => err);
        }),
        finalize(() => {
          this.refreshInFlight$ = null;
        }),
        shareReplay(1),
      );

    return this.refreshInFlight$;
  }

  logout(): Observable<void> {
    const storedRefreshToken = localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    this.clearSession();

    if (!storedRefreshToken) {
      return of(undefined);
    }

    // Best-effort: whether or not the server-side revoke succeeds, the user is logged out locally
    // either way (tokens are already cleared above) - we don't want a flaky network call to strand
    // someone in a "can't log out" state.
    return this.http
      .post<void>(`${environment.apiUrl}/api/auth/logout`, { refreshToken: storedRefreshToken })
      .pipe(catchError(() => of(undefined)));
  }

  /** Synchronous read for the HTTP interceptor, which isn't a component/service and so reads the
   * signal's current value directly with `()` rather than through a template binding. */
  getAccessToken(): string | null {
    return this.accessToken();
  }

  clearSession(): void {
    this.accessToken.set(null);
    localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
  }

  private applySession(tokens: AuthTokens): void {
    this.accessToken.set(tokens.accessToken);

    // ── Token storage tradeoff (read this before changing it) ────────────────────────────────────
    // The access token lives ONLY in the `accessToken` signal above - never in localStorage/sessionStorage.
    // If this app ever had an XSS vulnerability (malicious script running in the page), that script
    // could read anything in localStorage; keeping the access token in memory only means it vanishes
    // the instant the tab closes or reloads, shrinking the window an attacker could exploit it in.
    //
    // The refresh token, below, IS put in localStorage - a deliberate, lesser-evil tradeoff for this
    // internal QA tool: without persisting it somewhere, every page refresh would force a re-login,
    // which is bad enough UX that we accept the (smaller, since it must also be exchanged with the
    // server to be useful) risk. A production-hardened version would instead have the backend set the
    // refresh token as an httpOnly cookie - one JavaScript literally cannot read at all, even with
    // XSS - but that requires the backend to issue/read cookies instead of a JSON body field, which is
    // a backend change out of scope for this frontend pass.
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, tokens.refreshToken);
  }
}

/** JWTs are three base64url-encoded segments joined by dots: header.payload.signature. We only need
 * the middle one (the claims) to know who's logged in - we never need to verify the signature
 * client-side, since the browser can't keep a signing key secret anyway; the backend re-verifies the
 * signature on every request regardless of what this decodes to. */
function decodeAccessTokenClaims(token: string): CurrentUser | null {
  try {
    const payloadSegment = token.split('.')[1];
    const json = atob(payloadSegment.replace(/-/g, '+').replace(/_/g, '/'));
    const claims = JSON.parse(json);
    return { id: claims['sub'], email: claims['email'], roles: normalizeRoleClaim(claims['role']) };
  } catch {
    return null;
  }
}

/** The JWT library that writes this token collapses multiple `Claim("role", ...)` entries into a
 * JSON array automatically - but a token with exactly ONE role serializes that claim as a plain
 * string, not a one-element array, and a token with none omits the key entirely. This normalizes all
 * three shapes into a single `string[]` so the rest of the app never has to think about which one it
 * got. */
function normalizeRoleClaim(role: unknown): string[] {
  if (Array.isArray(role)) {
    return role;
  }
  return typeof role === 'string' ? [role] : [];
}
