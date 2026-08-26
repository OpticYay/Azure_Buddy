import { Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';

import { DisplayMessage } from '../../../core/models/display-message.model';
import { MarkdownLitePipe } from '../../../shared/markdown-lite.pipe';
import { isWorkItemIdColumn, workItemIdCellUrl } from '../../../shared/work-item-id-column';

// This renders as a typed log entry rather than a chat bubble: the backend tags every message with a
// real type (Text, Table, Confirmation, Error - see ChatMessageType), and the most valuable of those
// are a results table and a "work item #123 recorded" confirmation, neither of which belongs inside a
// speech balloon. Each entry instead carries a role, a state tag, and a timestamp.
@Component({
  selector: 'app-message-item',
  imports: [DatePipe, MarkdownLitePipe],
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
    return workItemIdCellUrl(header, cellValue, this.adoWorkItemBaseUrl());
  }

  isIdColumn(header: string): boolean {
    return isWorkItemIdColumn(header);
  }
}
