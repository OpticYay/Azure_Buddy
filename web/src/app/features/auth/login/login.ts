import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AuthService } from '../../../core/services/auth.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';

// ── What is a "reactive form" in Angular? ───────────────────────────────────────────────────────
// Angular offers two ways to build forms. "Template-driven" forms declare validation/binding directly
// in the HTML (`[(ngModel)]`, etc.) - fine for very simple forms. "Reactive" forms (used throughout
// this app) instead build the form's structure as an explicit object IN THE COMPONENT CLASS (a
// `FormGroup` containing `FormControl`s), and the template just binds to that existing object. This
// is more verbose for a two-field form like this one, but scales much better and is easier to unit
// test, since the form's state/validation logic lives in plain TypeScript instead of being spread
// across template attributes - and it's the pattern you'll see in any real-world Angular codebase.
@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  // `inject()` reads a dependency from Angular's DI system right where it's used, instead of through
  // a constructor parameter. It must run synchronously during component construction (never inside a
  // setTimeout, a subscribe callback, etc.), which field initializers satisfy - and unlike constructor
  // parameters, field initializers using inject() run in the order they're WRITTEN, so `fb` below is
  // guaranteed ready before `form` tries to use it. This sidesteps a real trap: if `fb` were instead a
  // constructor-injected parameter, `readonly form = this.fb.group(...)` would fail, because field
  // initializers all run BEFORE the constructor body does - `this.fb` wouldn't be assigned yet.
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  // Local UI state for this component only - unlike AuthService's signals, nothing outside this
  // component needs to know whether a login request is currently in flight, so a plain signal() owned
  // by the component (not a shared service) is the right scope for it.
  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      // markAllAsTouched() makes Angular show validation error messages for every field, even ones
      // the user never clicked into - otherwise clicking "Log in" on an empty form would silently do
      // nothing, with no visible feedback about why.
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
        this.errorMessage.set(extractApiErrorMessage(err.error, 'Login failed. Please check your credentials and try again.'));
      },
    });
  }
}
