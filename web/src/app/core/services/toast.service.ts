import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  message: string;
  kind: ToastKind;
}

const AUTO_DISMISS_MS = 4000;

/** App-wide toast notifications - a small floating message that appears, then disappears on its own
 * a few seconds later. Used for "fire and forget" confirmations (settings saved) and errors that
 * don't belong next to a specific form field. Any component can call `toast.success(...)` /
 * `toast.error(...)`; ToastContainer (mounted once in AppShell) is the only thing that reads the
 * `toasts` signal and actually renders them - the same "one shared signal, many readers" pattern
 * AuthService's `currentUser` uses. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 0;
  readonly toasts = signal<Toast[]>([]);

  success(message: string): void {
    this.show(message, 'success');
  }

  error(message: string): void {
    this.show(message, 'error');
  }

  info(message: string): void {
    this.show(message, 'info');
  }

  dismiss(id: number): void {
    this.toasts.update((existing) => existing.filter((t) => t.id !== id));
  }

  private show(message: string, kind: ToastKind): void {
    const id = this.nextId++;
    this.toasts.update((existing) => [...existing, { id, message, kind }]);
    setTimeout(() => this.dismiss(id), AUTO_DISMISS_MS);
  }
}
