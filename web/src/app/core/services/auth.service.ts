import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, finalize, map, of, shareReplay, tap, throwError } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  AuthTokens,
  ConfirmEmailRequest,
  ForgotPasswordRequest,
  LoginRequest,
  RegisterRequest,
  ResendConfirmationRequest,
  ResetPasswordRequest,
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

@Injectable({ providedIn: 'root' })
export class AuthService {
  // Holds the current JWT access token in memory ONLY - see applySession's comment for why not
  // localStorage.
  private readonly accessToken = signal<string | null>(null);

  readonly currentUser = computed<CurrentUser | null>(() => {
    const token = this.accessToken();
    return token ? decodeAccessTokenClaims(token) : null;
  });

  readonly isAuthenticated = computed(() => this.currentUser() !== null);

  /** Read by adminGuard and AppShell's nav to show/gate the LLM settings screen. Derives from
   * currentUser() the same way isAuthenticated does - there is no separate "am I admin" API call,
   * the answer already arrived encoded in the JWT the moment the user logged in. */
  readonly isAdmin = computed(() => this.currentUser()?.roles.includes('Admin') ?? false);

  // Multiple HTTP requests can 401 around the same moment (e.g. a page firing 3 API calls on load,
  // all with an expired token). Without this, each would independently kick off its own
  // POST /api/auth/refresh - wasteful, and could even race and invalidate each other's new tokens.
  // This field remembers an in-flight refresh call so every 401 can share the SAME one.
  private refreshInFlight$: Observable<AuthTokens> | null = null;

  constructor(private readonly http: HttpClient) {}

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

  /** Always resolves (204) whether or not the email has an account - see the backend's
   * AuthService.ForgotPasswordAsync docs for why. The caller should show the same "check your email"
   * message either way, never branch UI on this succeeding vs. failing. */
  forgotPassword(request: ForgotPasswordRequest): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/api/auth/forgot-password`, request);
  }

  /** Unlike register/login, a successful reset does NOT call applySession - resetting a password is
   * not implicitly "log me in on this device", so the returned tokens are intentionally unused here.
   * The reset-password page sends the user to /login to sign in explicitly with the new password. */
  resetPassword(request: ResetPasswordRequest): Observable<AuthTokens> {
    return this.http.post<AuthTokens>(`${environment.apiUrl}/api/auth/reset-password`, request);
  }

  /** Confirming DOES log the user in immediately (applySession) - unlike resetPassword, clicking a
   * confirmation link is naturally "I'm here, on this device, right now," so there's no reason to
   * make them re-enter credentials afterward. */
  confirmEmail(request: ConfirmEmailRequest): Observable<AuthTokens> {
    return this.http
      .post<AuthTokens>(`${environment.apiUrl}/api/auth/confirm-email`, request)
      .pipe(tap((tokens) => this.applySession(tokens)));
  }

  resendConfirmation(request: ResendConfirmationRequest): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/api/auth/resend-confirmation`, request);
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
      map(() => undefined),
      // A failed silent refresh should look like "not logged in," not crash app boot.
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

    // `shareReplay(1)` runs the source ONCE and replays that result to every subscriber, which is what
    // makes the de-duplication above work. `finalize` clears refreshInFlight$ once the call settles so
    // the *next* 401, later, correctly starts a new refresh instead of replaying a stale one forever.
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

  /** Used by AccountService's changePassword call so the backend can spare THIS session's refresh
   * token from the other-sessions revocation it does on a successful password change - see
   * ChangePasswordRequest's docs. Null if there's no stored session, same as a logged-out state. */
  getRefreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
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
