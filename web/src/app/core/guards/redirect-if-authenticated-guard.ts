import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../services/auth.service';

/** The mirror image of authGuard: keeps an already-logged-in user OFF /login and /register (there's
 * nothing for them to do there) by bouncing them to /chat instead. */
export const redirectIfAuthenticatedGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAuthenticated() ? router.createUrlTree(['/chat']) : true;
};
