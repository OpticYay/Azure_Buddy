import { Component, OnInit, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import { AccountService } from '../../core/services/account.service';
import { AuthService } from '../../core/services/auth.service';
import { ProfileView } from '../../core/models/account.models';
import { extractApiErrorMessage, extractFieldErrors } from '../../core/models/api-error.model';
import { ToastService } from '../../core/services/toast.service';

/// Group-level validator (not on one control) since it compares two sibling fields - Angular has no
/// built-in "these two fields must match" validator. Attaches its error to the FormGroup itself, read
/// in the template via passwordForm.hasError('mismatch'). Checked client-side before ever calling the
/// backend, per the task's own "fast feedback matters here" - the backend's own password policy check
/// only ever sees newPassword, it has no opinion on whether confirmNewPassword matched it.
function passwordsMatchValidator(group: AbstractControl): ValidationErrors | null {
  const newPassword = group.get('newPassword')?.value;
  const confirmNewPassword = group.get('confirmNewPassword')?.value;
  return newPassword === confirmNewPassword ? null : { mismatch: true };
}

// Self-service account screen: view/edit your own display name and email, and change your own
// password. Two independent cards/forms on one page - see profile.html.
@Component({
  selector: 'app-profile',
  imports: [ReactiveFormsModule],
  templateUrl: './profile.html',
  styleUrl: './profile.css',
})
export class Profile implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly accountService = inject(AccountService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly profile = signal<ProfileView | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly isEditingProfile = signal(false);
  readonly savingProfile = signal(false);
  readonly profileError = signal<string | null>(null);

  readonly profileForm = this.fb.group({
    displayName: ['', [Validators.required]],
    email: ['', [Validators.required, Validators.email]],
  });

  readonly changingPassword = signal(false);
  readonly passwordError = signal<string | null>(null);
  readonly passwordFieldErrors = signal<Record<string, string>>({});

  readonly passwordForm = this.fb.group(
    {
      currentPassword: ['', [Validators.required]],
      newPassword: ['', [Validators.required, Validators.minLength(8)]],
      confirmNewPassword: ['', [Validators.required]],
    },
    { validators: passwordsMatchValidator },
  );

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.accountService.getProfile().subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Could not load your profile.');
      },
    });
  }

  startEditingProfile(): void {
    const current = this.profile();
    this.profileForm.reset({
      displayName: current?.displayName ?? '',
      email: current?.email ?? '',
    });
    this.profileError.set(null);
    this.isEditingProfile.set(true);
  }

  cancelEditingProfile(): void {
    this.isEditingProfile.set(false);
    this.profileError.set(null);
  }

  saveProfile(): void {
    if (this.profileForm.invalid || this.savingProfile()) {
      this.profileForm.markAllAsTouched();
      return;
    }

    this.savingProfile.set(true);
    this.profileError.set(null);

    const { displayName, email } = this.profileForm.getRawValue();

    this.accountService.updateProfile({ displayName: displayName!, email: email! }).subscribe({
      next: (response) => {
        this.savingProfile.set(false);

        if (response.requiresReLogin) {
          // The JWT's email claim is stale the moment the email changes (baked in at mint time - see
          // UpdateProfileResponse's docs) - force a clean re-login now rather than leave the old
          // address showing in the top bar for up to the access token's remaining lifetime.
          this.toast.success('Email updated. Please sign in again with your new email.');
          this.auth.logout().subscribe(() => this.router.navigateByUrl('/login'));
          return;
        }

        this.profile.set(response.profile);
        this.isEditingProfile.set(false);
        this.toast.success('Profile updated.');
      },
      error: (err: HttpErrorResponse) => {
        this.savingProfile.set(false);
        this.profileError.set(extractApiErrorMessage(err.error, 'Could not update your profile.'));
      },
    });
  }

  changePassword(): void {
    if (this.passwordForm.invalid || this.changingPassword()) {
      this.passwordForm.markAllAsTouched();
      return;
    }

    this.changingPassword.set(true);
    this.passwordError.set(null);
    this.passwordFieldErrors.set({});

    const { currentPassword, newPassword } = this.passwordForm.getRawValue();

    this.accountService.changePassword(currentPassword!, newPassword!).subscribe({
      next: () => {
        this.changingPassword.set(false);
        this.passwordForm.reset();
        // This session's own tokens are untouched - AccountService (backend) only revokes OTHER
        // active refresh tokens, sparing the one this very request was authenticated with. Nothing to
        // re-authenticate here, unlike the forced re-login after an email change above.
        this.toast.success('Password changed. Your other signed-in sessions have been signed out.');
      },
      error: (err: HttpErrorResponse) => {
        this.changingPassword.set(false);
        const fieldErrors = extractFieldErrors(err.error);
        this.passwordFieldErrors.set(fieldErrors);
        // Only fall back to the generic banner when nothing could be pinned to a specific field -
        // otherwise the field-level message already covers it and a duplicate banner is just noise.
        this.passwordError.set(
          Object.keys(fieldErrors).length > 0 ? null : extractApiErrorMessage(err.error, 'Could not change your password.'),
        );
      },
    });
  }
}
