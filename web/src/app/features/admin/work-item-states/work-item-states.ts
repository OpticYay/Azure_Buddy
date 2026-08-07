import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { WorkItemStatesService } from '../../../core/services/work-item-states.service';
import { WorkItemStateView, WorkItemTypeStatesView } from '../../../core/models/work-item-states.models';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { ToastService } from '../../../core/services/toast.service';
import { popIn } from '../../../shared/animations';

/** Local editable copy of one row, kept separate from the server's WorkItemStateView so typing in the
 * edit fields doesn't mutate the last-loaded data until Save actually succeeds. */
interface EditDraft {
  stateName: string;
  displayOrder: number;
  isEnabled: boolean;
}

/** Local "add a state" form state, one per work item type group (plus one for a brand new type). */
interface AddDraft {
  workItemType: string;
  stateName: string;
  displayOrder: number;
}

// Admin screen for the per-work-item-type valid state list (WorkItemStateConfigurations) - same
// page shape as the LLM settings admin screen (load/error/card), but this data is a list of groups
// rather than a single settings row, so the UI is a table-per-type instead of a form.
@Component({
  selector: 'app-work-item-states',
  imports: [FormsModule],
  templateUrl: './work-item-states.html',
  styleUrl: './work-item-states.css',
  animations: [popIn],
})
export class WorkItemStates implements OnInit {
  private readonly service = inject(WorkItemStatesService);
  private readonly toast = inject(ToastService);

  readonly groups = signal<WorkItemTypeStatesView[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly editingId = signal<number | null>(null);
  readonly editDraft = signal<EditDraft>({ stateName: '', displayOrder: 0, isEnabled: true });
  readonly savingId = signal<number | null>(null);

  // Which type's inline "add a state" row is expanded, plus its draft.
  readonly addingForType = signal<string | null>(null);
  readonly addDraft = signal<AddDraft>({ workItemType: '', stateName: '', displayOrder: 0 });
  readonly adding = signal(false);

  // A brand new work item type has no existing group to attach an "add" row to.
  readonly addingNewType = signal(false);
  readonly newTypeDraft = signal<AddDraft>({ workItemType: '', stateName: '', displayOrder: 0 });

  readonly actionError = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.service.getAll().subscribe({
      next: (groups) => {
        this.groups.set(groups);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Could not load work item states.');
      },
    });
  }

  startEdit(row: WorkItemStateView): void {
    this.editingId.set(row.id);
    this.editDraft.set({ stateName: row.stateName, displayOrder: row.displayOrder, isEnabled: row.isEnabled });
    this.actionError.set(null);
  }

  cancelEdit(): void {
    this.editingId.set(null);
    this.actionError.set(null);
  }

  saveEdit(row: WorkItemStateView): void {
    const draft = this.editDraft();
    if (!draft.stateName.trim() || this.savingId() !== null) {
      return;
    }

    this.savingId.set(row.id);
    this.actionError.set(null);

    this.service
      .update(row.id, { stateName: draft.stateName.trim(), displayOrder: draft.displayOrder, isEnabled: draft.isEnabled })
      .subscribe({
        next: () => {
          this.savingId.set(null);
          this.editingId.set(null);
          this.toast.success('State updated.');
          this.load();
        },
        error: (err: HttpErrorResponse) => {
          this.savingId.set(null);
          this.actionError.set(extractApiErrorMessage(err.error, 'Could not update this state.'));
        },
      });
  }

  toggleEnabled(row: WorkItemStateView): void {
    if (this.savingId() !== null) {
      return;
    }
    this.savingId.set(row.id);
    this.service
      .update(row.id, { stateName: row.stateName, displayOrder: row.displayOrder, isEnabled: !row.isEnabled })
      .subscribe({
        next: () => {
          this.savingId.set(null);
          this.load();
        },
        error: (err: HttpErrorResponse) => {
          this.savingId.set(null);
          this.actionError.set(extractApiErrorMessage(err.error, 'Could not update this state.'));
        },
      });
  }

  deleteState(row: WorkItemStateView): void {
    if (this.savingId() !== null) {
      return;
    }
    this.savingId.set(row.id);
    this.service.delete(row.id).subscribe({
      next: () => {
        this.savingId.set(null);
        this.toast.success(`Removed "${row.stateName}".`);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.savingId.set(null);
        this.actionError.set(extractApiErrorMessage(err.error, 'Could not remove this state.'));
      },
    });
  }

  startAdd(group: WorkItemTypeStatesView): void {
    this.addingForType.set(group.workItemType);
    this.addDraft.set({
      workItemType: group.workItemType,
      stateName: '',
      displayOrder: group.states.length,
    });
    this.actionError.set(null);
  }

  cancelAdd(): void {
    this.addingForType.set(null);
    this.actionError.set(null);
  }

  submitAdd(): void {
    const draft = this.addDraft();
    if (!draft.stateName.trim() || this.adding()) {
      return;
    }

    this.adding.set(true);
    this.actionError.set(null);

    this.service
      .create({ workItemType: draft.workItemType, stateName: draft.stateName.trim(), displayOrder: draft.displayOrder, isEnabled: true })
      .subscribe({
        next: () => {
          this.adding.set(false);
          this.addingForType.set(null);
          this.toast.success(`Added "${draft.stateName.trim()}" to ${draft.workItemType}.`);
          this.load();
        },
        error: (err: HttpErrorResponse) => {
          this.adding.set(false);
          this.actionError.set(extractApiErrorMessage(err.error, 'Could not add this state.'));
        },
      });
  }

  startAddType(): void {
    this.addingNewType.set(true);
    this.newTypeDraft.set({ workItemType: '', stateName: '', displayOrder: 0 });
    this.actionError.set(null);
  }

  cancelAddType(): void {
    this.addingNewType.set(false);
    this.actionError.set(null);
  }

  submitAddType(): void {
    const draft = this.newTypeDraft();
    if (!draft.workItemType.trim() || !draft.stateName.trim() || this.adding()) {
      return;
    }

    this.adding.set(true);
    this.actionError.set(null);

    this.service
      .create({ workItemType: draft.workItemType.trim(), stateName: draft.stateName.trim(), displayOrder: 0, isEnabled: true })
      .subscribe({
        next: () => {
          this.adding.set(false);
          this.addingNewType.set(false);
          this.toast.success(`Added new type "${draft.workItemType.trim()}".`);
          this.load();
        },
        error: (err: HttpErrorResponse) => {
          this.adding.set(false);
          this.actionError.set(extractApiErrorMessage(err.error, 'Could not add this work item type.'));
        },
      });
  }
}
