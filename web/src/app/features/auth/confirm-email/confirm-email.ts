import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AuthService } from '../../../core/services/auth.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';

type ConfirmState = 'confirming' | 'success' | 'error' | 'invalid-link';

@Component({
  selector: 'app-confirm-email',
  imports: [RouterLink],
  templateUrl: './confirm-email.html',
  styleUrl: './confirm-email.css',
})
export class ConfirmEmail implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly state = signal<ConfirmState>('confirming');
  readonly errorMessage = signal<string | null>(null);

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    const email = params.get('email');
    const token = params.get('token');

    if (!email || !token) {
      this.state.set('invalid-link');
      return;
    }

    // confirmEmail() logs the user in on success (see AuthService's docs) - clicking a confirmation
    // link is naturally "I'm here, right now," so send them straight into the app rather than making
    // them log in again immediately after.
    this.auth.confirmEmail({ email, token }).subscribe({
      next: () => {
        this.state.set('success');
        this.router.navigateByUrl('/chat');
      },
      error: (err: HttpErrorResponse) => {
        this.state.set('error');
        this.errorMessage.set(extractApiErrorMessage(err.error, 'This confirmation link is invalid or has expired.'));
      },
    });
  }
}
