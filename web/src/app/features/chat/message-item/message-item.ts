import { Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';

import { DisplayMessage } from '../../../core/models/display-message.model';

// ── What a component input is ───────────────────────────────────────────────────────────────────
// Most components in this app manage their own state - they fetch their own data and own their own
// signals. This one is pure presentation: MessageThread renders one <app-message-item> per message
// and hands each its data through attribute-style bindings:
//     <app-message-item [displayMessage]="msg" [adoWorkItemBaseUrl]="url()" />
// `input.required<T>()` means the component cannot be used without that value being passed (Angular
// enforces it at compile time); `input<T>(default)` declares an optional one.
//
// ── Why this renders a log entry rather than a chat bubble ──────────────────────────────────────
// The backend tags every message with a real type - Text, Table, Confirmation or Error (see
// ChatMessageType) - because deterministic flows know exactly what they produced. The most valuable
// of those are a results table and a "work item #123 recorded" confirmation, and neither belongs
// inside a speech balloon. So the thread is set as a typed log: each entry carries a role, a state
// tag, and a timestamp, and the structured payloads sit in it naturally.
@Component({
  selector: 'app-message-item',
  imports: [DatePipe],
  templateUrl: './message-item.html',
  styleUrl: './message-item.css',
})
export class MessageItem {
  readonly displayMessage = input.required<DisplayMessage>();

  /** e.g. "https://dev.azure.com/my-org/MyProject/_workitems/edit" - null until the user's ADO
   * settings load, or if they haven't configured a connection yet. When null, work item IDs render
   * as plain (unlinked) stamps rather than links that would 404. */
  readonly adoWorkItemBaseUrl = input<string | null>(null);

  // `role` arrives as the string "User" / "Assistant" (the API serializes enums by name), so this
  // is a direct comparison - no number-to-name lookup table needed.
  readonly isUser = computed(() => this.displayMessage().message.role === 'User');

  readonly roleLabel = computed(() => (this.isUser() ? 'You' : 'Buddy'));

  /** The state tag shown in the entry header. Deliberately in the product's own vocabulary -
   * a QA engineer files evidence and records defects - rather than restating the internal enum
   * name. Plain text replies get no tag at all: a label that appears on everything says nothing. */
  readonly tagLabel = computed<string | null>(() => {
    switch (this.displayMessage().type) {
      case 'table':
        return 'Results';
      case 'confirmation':
        return 'Recorded';
      case 'error':
        return 'Failed';
      case 'screenshot':
        return 'Evidence';
      default:
        return null;
    }
  });

  workItemUrl(id: number): string | null {
    const base = this.adoWorkItemBaseUrl();
    return base ? `${base}/${id}` : null;
  }

  /** Table cells are all strings. A cell becomes a linked work item stamp only when its column is
   * the ID column and the value is a whole number - so a title that happens to be numeric doesn't
   * get turned into a broken link. */
  cellWorkItemUrl(header: string, cellValue: string): string | null {
    if (header.trim().toUpperCase() !== 'ID' || !/^\d+$/.test(cellValue.trim())) {
      return null;
    }
    return this.workItemUrl(Number(cellValue));
  }

  isIdColumn(header: string): boolean {
    return header.trim().toUpperCase() === 'ID';
  }
}
