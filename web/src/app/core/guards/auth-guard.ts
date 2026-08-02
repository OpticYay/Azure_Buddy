import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../services/auth.service';

// ── What is a route guard? ──────────────────────────────────────────────────────────────────────
// A guard is a function the Router runs BEFORE actually navigating to a route, deciding whether that
// navigation is allowed to proceed. Returning `true` lets it through; returning `false` (or, as here,
// a UrlTree) cancels the original navigation and redirects somewhere else instead. `CanActivateFn` is
// the functional style (the modern replacement for an older class-based CanActivate interface) - like
// the interceptor, it's just a plain function, using `inject()` to reach the services it needs.
//
// This one is attached to the AppShell layout route in app.routes.ts (`canActivate: [authGuard]`),
// which - because it's the PARENT of the chat/settings child routes - protects all of them at once;
// an unauthenticated visit to /chat or /settings/ado redirects to /login instead of ever rendering.
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  // `state.url` is the URL the user was trying to reach - passed along as a query param so the login
  // page can send them back there after a successful login instead of always landing on /chat.
  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
