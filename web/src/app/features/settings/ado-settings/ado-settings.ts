import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { AdoSettingsService } from '../../../core/services/ado-settings.service';
import { AdoSettingsView, TestConnectionResult } from '../../../core/models/settings.models';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { ToastService } from '../../../core/services/toast.service';
import { popIn } from '../../../shared/animations';

@Component({
  selector: 'app-ado-settings',
  imports: [ReactiveFormsModule],
  templateUrl: './ado-settings.html',
  styleUrl: './ado-settings.css',
  animations: [popIn],
})
export class AdoSettings implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly settingsService = inject(AdoSettingsService);
  private readonly toast = inject(ToastService);

  readonly settings = signal<AdoSettingsView | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  // Whether the org-url/project/PAT fields are currently shown as an editable form. Starts false;
  // load() flips it to true automatically if there's nothing saved yet (first-time setup).
  readonly isEditing = signal(false);
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  readonly deleting = signal(false);
  readonly testing = signal(false);
  readonly testResult = signal<TestConnectionResult | null>(null);

  readonly form = this.fb.group({
    organizationUrl: ['', [Validators.required, Validators.pattern(/^https?:\/\/.+/)]],
    defaultProject: ['', [Validators.required]],
    // Required every time the form is submitted, even if the user is only changing the project name -
    // see the big comment on save() below for why that's a real backend constraint, not our choice.
    personalAccessToken: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.settingsService.get().subscribe({
      next: (view) => {
        this.settings.set(view);
        this.loading.set(false);
        if (!view.isConfigured) {
          // Nothing saved yet - go straight to the editable form instead of showing an empty "view" state.
          this.startEditing();
        }
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Could not load your ADO settings. Check your connection and try again.');
      },
    });
  }

  startEditing(): void {
    const current = this.settings();
    this.form.reset({
      organizationUrl: current?.organizationUrl ?? '',
      defaultProject: current?.defaultProject ?? '',
      personalAccessToken: '',
    });
    this.saveError.set(null);
    this.testResult.set(null);
    this.isEditing.set(true);
  }

  /** Only reachable when settings().isConfigured - there's nothing to "cancel" back to otherwise. */
  cancelEditing(): void {
    this.isEditing.set(false);
    this.saveError.set(null);
  }

  save(): void {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    // Why the PAT field is required on EVERY save, not just the first: SaveAdoSettingsRequest on the
    // backend (see AzureBuddy.Core/Settings/AdoSettingsModels.cs) marks PersonalAccessToken [Required]
    // unconditionally - there is no "PUT with just organizationUrl/defaultProject, leave the existing
    // PAT alone" option. That's not an oversight to work around here; it's because the backend only
    // ever stores an ENCRYPTED PAT and never decrypts it back out to compare/reuse - the simplest way
    // to guarantee that stays true is to never accept a partial update at all.
    const { organizationUrl, defaultProject, personalAccessToken } = this.form.getRawValue();

    this.settingsService
      .save({
        organizationUrl: organizationUrl!,
        defaultProject: defaultProject!,
        personalAccessToken: personalAccessToken!,
      })
      .subscribe({
        next: (view) => {
          this.settings.set(view);
          this.isEditing.set(false);
          this.saving.set(false);
          // An action keeps its name through the whole flow: the button says "Save connection", so
          // the confirmation says "Connection saved" - not a differently-worded near-synonym.
          this.toast.success('Connection saved.');
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          this.saveError.set(extractApiErrorMessage(err.error, 'Could not save your ADO settings.'));
        },
      });
  }

  testConnection(): void {
    this.testing.set(true);
    this.testResult.set(null);

    this.settingsService.testConnection().subscribe({
      next: (result) => {
        this.testing.set(false);
        this.testResult.set(result);
      },
      error: (err: HttpErrorResponse) => {
        this.testing.set(false);
        this.testResult.set({
          success: false,
          error: extractApiErrorMessage(err.error, 'The test-connection request itself failed.'),
        });
      },
    });
  }

  confirmDelete(): void {
    // A plain browser confirm() dialog - blunt, but exactly the "confirmation prompt before firing"
    // this destructive action needs, without pulling in a whole modal-dialog component for one button.
    if (!confirm('Remove this connection? Buddy will not be able to reach Azure DevOps until you add it again.')) {
      return;
    }

    this.deleting.set(true);
    this.settingsService.delete().subscribe({
      next: () => {
        this.deleting.set(false);
        this.settings.set({ isConfigured: false, organizationUrl: null, defaultProject: null, maskedPat: null, updatedAt: null });
        this.startEditing();
        this.toast.info('Connection removed.');
      },
      error: () => {
        this.deleting.set(false);
        this.loadError.set('Could not delete your ADO settings. Please try again.');
      },
    });
  }
}
