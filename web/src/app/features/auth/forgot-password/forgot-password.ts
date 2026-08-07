import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AuthService } from '../../../core/services/auth.service';

@Component({
  selector: 'app-forgot-password',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './forgot-password.html',
  styleUrl: './forgot-password.css',
})
export class ForgotPassword {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
  });

  readonly isSubmitting = signal(false);
  // No separate error state: the backend always returns success here regardless of whether the email
  // has an account (see AuthService.forgotPassword's docs) - the only way this doesn't reach `submitted`
  // is a genuine network/server failure, and even then showing the same "check your email" message is
  // the safer default (it never reveals account existence either way).
  readonly submitted = signal(false);

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    const { email } = this.form.getRawValue();

    this.auth.forgotPassword({ email: email! }).subscribe({
      // next and error both land on the same "check your email" state, deliberately - see the
      // `submitted` field comment above.
      next: () => {
        this.isSubmitting.set(false);
        this.submitted.set(true);
      },
      error: () => {
        this.isSubmitting.set(false);
        this.submitted.set(true);
      },
    });
  }
}
