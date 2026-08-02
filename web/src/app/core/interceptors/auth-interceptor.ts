import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';

import { AuthService } from '../services/auth.service';

// ── What is an HTTP interceptor? ────────────────────────────────────────────────────────────────
// Every request HttpClient sends, and every response it receives, passes through a chain of
// interceptor functions before reaching your code - like middleware on the backend (ASP.NET Core's
// own pipeline, e.g. this project's UseAuthentication/UseAuthorization/UseCors, is the same idea on
// the server side). This one does two jobs: (1) attach the current JWT to every outgoing request that
// needs one, so individual services never have to remember to do it themselves, and (2) if a request
// comes back 401 Unauthorized (access token expired), silently get a new one and retry - so an
// expired token, mid-session, doesn't dump the user back to the login screen for no visible reason.
//
// `HttpInterceptorFn` is a plain function, not a class - this is Angular's newer "functional
// interceptor" style (replacing an older class-based HttpInterceptor interface). `inject(...)` is how
// a function (which has no constructor to receive dependencies the way a class does) still participates
// in Angular's dependency injection - it must be called while Angular is actively setting up this
// function's execution context, which is true here since this only ever runs as part of the HTTP
// pipeline provideHttpClient(withInterceptors([...])) established in app.config.ts.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  // The auth endpoints themselves never need (and register/login can't yet have) a bearer token.
  const isAuthEndpoint = req.url.includes('/api/auth/');
  const requestToSend = isAuthEndpoint ? req : attachToken(req, auth.getAccessToken());

  return next(requestToSend).pipe(
    catchError((error: unknown) => {
      const isUnauthorized = error instanceof HttpErrorResponse && error.status === 401;
      if (!isUnauthorized || isAuthEndpoint) {
        // Not a 401, or it's the refresh/login call itself failing - nothing to retry, just propagate.
        return throwError(() => error);
      }

      // `switchMap` here means: run refreshAccessToken(), and once THAT observable emits, switch to
      // (subscribe to) a new observable built from its result - in this case, the original request
      // resent with the new token attached. If refreshAccessToken's observable errors (refresh token
      // itself expired/revoked), that error flows through to the catchError below instead.
      return auth.refreshAccessToken().pipe(
        switchMap((tokens) => next(attachToken(req, tokens.accessToken))),
        catchError((refreshError: unknown) => {
          auth.clearSession();
          router.navigateByUrl('/login');
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};

function attachToken(req: Parameters<HttpInterceptorFn>[0], token: string | null) {
  if (!token) {
    return req;
  }
  // HttpRequest objects are immutable (a deliberate RxJS/Angular design - the same request object
  // could otherwise be mutated by one interceptor in a way that surprises another). `.clone(...)`
  // returns a new request with just the given overrides applied, everything else copied as-is.
  return req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}
