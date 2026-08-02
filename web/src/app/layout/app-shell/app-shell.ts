import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthService } from '../../core/services/auth.service';
import { ToastContainer } from '../../shared/ui/toast-container/toast-container';

// The shared frame for every "logged in" page: a top bar (nav links, current user, logout) with a
// <router-outlet> underneath for whichever child route (chat, or settings/ado) is currently active.
// ToastContainer is mounted here once, rather than in every individual page, since toast notifications
// (settings saved, etc.) should float above whichever page is currently showing, not be scoped to one.
@Component({
  selector: 'app-app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastContainer],
  templateUrl: './app-shell.html',
  styleUrl: './app-shell.css',
})
export class AppShell {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  // Exposed directly to the template as a public field so app-shell.html can call `currentUser()` -
  // components commonly re-expose a service's signal like this rather than duplicating its logic.
  readonly currentUser = this.auth.currentUser;
  readonly isAdmin = this.auth.isAdmin;

  logout(): void {
    // We don't need to react to the logout call's result in the template - AuthService.logout()
    // already clears local session state as its very first step (see the service), so the UI should
    // navigate away immediately rather than waiting on the network call to the server to finish.
    this.auth.logout().subscribe();
    this.router.navigateByUrl('/login');
  }
}
