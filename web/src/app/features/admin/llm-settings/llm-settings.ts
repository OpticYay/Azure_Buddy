import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { LlmSettingsService } from '../../../core/services/llm-settings.service';
import { LlmSettingsView } from '../../../core/models/llm-settings.models';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { ToastService } from '../../../core/services/toast.service';
import { popIn } from '../../../shared/animations';

/// Same view/edit-mode shape as AdoSettings (see ado-settings.ts), but with one real difference in
/// what "editing" pre-fills: the Gemini API key field starts BLANK with a placeholder showing the
/// current mask (matches ado-settings' PAT field, which is genuinely a secret that must be re-typed
/// every time) - but every OTHER field (models, URLs, timeouts) pre-fills with its real current
/// value, because none of those are secret. There's no reason to make an admin retype a timeout just
/// to change a model name.
@Component({
  selector: 'app-llm-settings',
  imports: [ReactiveFormsModule],
  templateUrl: './llm-settings.html',
  styleUrl: './llm-settings.css',
  animations: [popIn],
})
export class LlmSettings implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly settingsService = inject(LlmSettingsService);
  private readonly toast = inject(ToastService);

  readonly settings = signal<LlmSettingsView | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly isEditing = signal(false);
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  readonly form = this.fb.group({
    primaryProvider: ['Gemini', [Validators.required]],
    useFallback: [true],
    geminiModel: ['', [Validators.required]],
    geminiBaseUrl: ['', [Validators.required, Validators.pattern(/^https?:\/\/.+/)]],
    geminiTimeoutSeconds: [30, [Validators.required, Validators.min(1), Validators.max(600)]],
    geminiApiKey: [''],
    ollamaModel: ['', [Validators.required]],
    ollamaBaseUrl: ['', [Validators.required, Validators.pattern(/^https?:\/\/.+/)]],
    ollamaNumCtx: [8192, [Validators.required, Validators.min(256), Validators.max(131072)]],
    ollamaTimeoutSeconds: [60, [Validators.required, Validators.min(1), Validators.max(600)]],
  });

  // Reading these off signals (rather than calling form.controls.x.value directly in the template)
  // is deliberate: this app is zoneless, so a plain method call in the template only re-evaluates
  // when *something else* triggers change detection. toSignal ties it to valueChanges, which is a
  // reliable, guaranteed re-render trigger (see message-composer.ts for the version of this bug
  // where a plain field write silently didn't reach the DOM).
  private readonly primaryProviderValue = toSignal(
    this.form.controls.primaryProvider.valueChanges,
    {
      initialValue: this.form.controls.primaryProvider.value,
    },
  );
  private readonly useFallbackValue = toSignal(this.form.controls.useFallback.valueChanges, {
    initialValue: this.form.controls.useFallback.value,
  });

  // A provider's fields only matter if Buddy will actually call it: as the primary, always; as the
  // fallback, only once "try the other provider" is switched on. Otherwise its fieldset just adds
  // noise to a form for a provider nothing will ever use - so it stays hidden, not merely disabled.
  readonly geminiFieldsVisible = computed(
    () => this.primaryProviderValue() === 'Gemini' || this.useFallbackValue(),
  );
  readonly ollamaFieldsVisible = computed(
    () => this.primaryProviderValue() === 'Ollama' || this.useFallbackValue(),
  );

  readonly viewGeminiVisible = computed(() => {
    const s = this.settings();
    return !!s && (s.primaryProvider === 'Gemini' || s.useFallback);
  });
  readonly viewOllamaVisible = computed(() => {
    const s = this.settings();
    return !!s && (s.primaryProvider === 'Ollama' || s.useFallback);
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
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Could not load model settings.');
      },
    });
  }

  startEditing(): void {
    const current = this.settings();
    this.form.reset({
      primaryProvider: current?.primaryProvider ?? 'Gemini',
      useFallback: current?.useFallback ?? true,
      geminiModel: current?.geminiModel ?? 'gemini-1.5-flash',
      geminiBaseUrl: current?.geminiBaseUrl ?? 'https://generativelanguage.googleapis.com/v1beta',
      geminiTimeoutSeconds: current?.geminiTimeoutSeconds ?? 30,
      geminiApiKey: '',
      ollamaModel: current?.ollamaModel ?? 'qwen2.5:14b-instruct',
      ollamaBaseUrl: current?.ollamaBaseUrl ?? 'http://localhost:11434',
      ollamaNumCtx: current?.ollamaNumCtx ?? 8192,
      ollamaTimeoutSeconds: current?.ollamaTimeoutSeconds ?? 60,
    });
    this.saveError.set(null);
    this.isEditing.set(true);
  }

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

    const raw = this.form.getRawValue();
    const trimmedKey = raw.geminiApiKey?.trim();

    this.settingsService
      .save({
        primaryProvider: raw.primaryProvider!,
        useFallback: raw.useFallback!,
        geminiModel: raw.geminiModel!,
        geminiBaseUrl: raw.geminiBaseUrl!,
        geminiTimeoutSeconds: raw.geminiTimeoutSeconds!,
        // Empty string and null both mean "unchanged" to the backend, but sending null (rather than
        // "") is the more honest representation of "the admin didn't type anything here."
        geminiApiKey: trimmedKey ? trimmedKey : null,
        ollamaModel: raw.ollamaModel!,
        ollamaBaseUrl: raw.ollamaBaseUrl!,
        ollamaNumCtx: raw.ollamaNumCtx!,
        ollamaTimeoutSeconds: raw.ollamaTimeoutSeconds!,
      })
      .subscribe({
        next: (view) => {
          this.settings.set(view);
          this.isEditing.set(false);
          this.saving.set(false);
          // "Immediately" is accurate, not marketing copy - see ILlmSettingsProvider.Refresh on the
          // backend, which this save call triggers synchronously before the response comes back.
          this.toast.success('Model settings saved — takes effect immediately, no restart needed.');
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          this.saveError.set(extractApiErrorMessage(err.error, 'Could not save model settings.'));
        },
      });
  }
}
