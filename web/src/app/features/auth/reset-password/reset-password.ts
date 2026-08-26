import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AuthService } from '../../../core/services/auth.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { ToastService } from '../../../core/services/toast.service';

@Component({
  selector: 'app-reset-password',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './reset-password.html',
  styleUrl: './reset-password.css',
})
export class ResetPassword implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  private email = '';
  private token = '';

  // True when the link itself is malformed (missing email/token query params) - distinct from a
  // rejected submit, which the form's own errorMessage handles. A link this broken can't even attempt
  // a reset, so the form never renders at all.
  readonly linkInvalid = signal(false);

  readonly form = this.fb.group({
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
  });

  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    this.email = params.get('email') ?? '';
    this.token = params.get('token') ?? '';
    this.linkInvalid.set(!this.email || !this.token);
  }

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const { newPassword } = this.form.getRawValue();

    // Deliberately does NOT log the user in (see AuthService.resetPassword's docs) - send them to
    // /login to sign in explicitly with their new password instead.
    this.auth
      .resetPassword({ email: this.email, token: this.token, newPassword: newPassword! })
      .subscribe({
        next: () => {
          this.toast.success('Password reset. Sign in with your new password.');
          this.router.navigateByUrl('/login');
        },
        error: (err: HttpErrorResponse) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(
            extractApiErrorMessage(
              err.error,
              'Could not reset your password. The link may have expired.',
            ),
          );
        },
      });
  }
}
