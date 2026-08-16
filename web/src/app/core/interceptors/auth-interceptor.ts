import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';

import { AuthService } from '../services/auth.service';

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
  // HttpRequest objects are immutable; .clone(...) returns a new request with the given overrides.
  return req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}
