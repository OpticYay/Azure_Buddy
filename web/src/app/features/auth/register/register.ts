import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AuthService } from '../../../core/services/auth.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './register.html',
  styleUrl: './register.css',
})
export class Register {
  // See login.ts for why these use inject() instead of constructor parameters - it's the same
  // "used before its initialization" trap otherwise, since `form` below needs `fb` immediately.
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly form = this.fb.group({
    displayName: ['', [Validators.required]],
    email: ['', [Validators.required, Validators.email]],
    // Matches the backend's password policy (Identity's PasswordOptions, see appsettings.json's
    // "Identity" section): min length 8, at least one digit, one lowercase, one uppercase. The backend
    // is the actual source of truth and re-validates regardless - this is just so a user finds out
    // their password is too weak immediately, instead of after a round trip to the server.
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const { email, password, displayName } = this.form.getRawValue();

    this.auth
      .register({ email: email!, password: password!, displayName: displayName! })
      .subscribe({
        next: () => this.router.navigateByUrl('/chat'),
        error: (err: HttpErrorResponse) => {
          this.isSubmitting.set(false);
          // Registration can fail for several distinct reasons at once (weak password AND duplicate
          // email, say) - the backend returns all of them, and extractApiErrorMessage joins them all
          // rather than showing just the first.
          this.errorMessage.set(
            extractApiErrorMessage(err.error, 'Registration failed. Please try again.'),
          );
        },
      });
  }
}
