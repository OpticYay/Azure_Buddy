import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AuthService } from '../../../core/services/auth.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  // Field initializers using inject() run in the order they're WRITTEN, so `fb` is guaranteed ready
  // before `form` below uses it - a constructor-injected `fb` would not be, since field initializers
  // all run before the constructor body does.
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      // Otherwise clicking "Log in" on an empty form would silently do nothing, with no visible feedback.
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const { email, password } = this.form.getRawValue();

    this.auth.login({ email: email!, password: password! }).subscribe({
      next: () => {
        // returnUrl was attached by authGuard when it redirected an unauthenticated visit here (see
        // core/guards/auth-guard.ts) - send the user back to wherever they were actually trying to go.
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/chat';
        this.router.navigateByUrl(returnUrl);
      },
      error: (err: HttpErrorResponse) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          extractApiErrorMessage(
            err.error,
            'Login failed. Please check your credentials and try again.',
          ),
        );
      },
    });
  }
}
