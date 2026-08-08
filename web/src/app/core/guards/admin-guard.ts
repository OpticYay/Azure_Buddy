import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../services/auth.service';

/** Redirects a non-admin to /chat rather than /login, since they ARE authenticated. Client-side UX
 * only, not the actual security boundary - LlmSettingsController's [Authorize(Roles = "Admin")] on
 * the backend is what a non-admin genuinely cannot get past. */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAdmin() ? true : router.createUrlTree(['/chat']);
};
