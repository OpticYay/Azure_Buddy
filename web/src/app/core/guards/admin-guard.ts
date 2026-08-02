import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../services/auth.service';

/** Gates /admin/llm. Layered UNDER authGuard on the route (see app.routes.ts) rather than replacing
 * it - this only ever runs once authGuard has already confirmed the user is logged in at all, so it
 * only needs to ask the one further question: are they an admin. A non-admin signed-in user is
 * redirected to /chat (there's nothing for them to do on an admin screen, same reasoning as
 * redirectIfAuthenticatedGuard bouncing a signed-in user off /login) rather than /login, since they
 * ARE authenticated - sending them to a login screen they'd just bounce off of again would be
 * confusing. This is client-side UX only, not the actual security boundary: LlmSettingsController's
 * [Authorize(Roles = "Admin")] on the backend is what a non-admin genuinely cannot get past, even if
 * they somehow navigated here directly or called the API by hand. */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAdmin() ? true : router.createUrlTree(['/chat']);
};
