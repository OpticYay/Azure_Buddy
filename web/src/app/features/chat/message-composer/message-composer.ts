import { Component, ElementRef, OnDestroy, ViewChild, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { ChatService } from '../../../core/services/chat.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { popIn } from '../../../shared/animations';

// ── What is a component output? ─────────────────────────────────────────────────────────────────
// The mirror image of `input()`: `output<void>()` lets THIS component notify its PARENT that
// something happened, without the parent handing it a callback function or this component needing to
// know anything about the parent. The parent listens with the same event-binding syntax as a native
// DOM event: `<app-message-composer (messageSent)="onMessageSent()" />`. We emit after every
// successful send (text or screenshot) so MessageThread knows to reload the message list - the
// composer's job ends at "the backend now has this message," not "update the thread's own state."
@Component({
  selector: 'app-message-composer',
  imports: [FormsModule],
  templateUrl: './message-composer.html',
  styleUrl: './message-composer.css',
  animations: [popIn],
})
export class MessageComposer implements OnDestroy {
  private readonly chatService = inject(ChatService);

  readonly sessionId = input.required<string>();
  readonly messageSent = output<void>();

  /** Fired only around the text-send path (POST /chat), true right before the request goes out and
   * false once it settles - MessageThread listens for this to show/hide the "agent is thinking" typing
   * indicator while the reply is in flight. Not used for screenshot sends, since a screenshot upload
   * has its own "Sending…" button state already and doesn't produce an agent reply to wait for. */
  readonly awaitingReply = output<boolean>();

  // ── Why these are signals, not plain string fields ──────────────────────────────────────────────
  // This app runs ZONELESS (there's no zone.js dependency - Angular 22's default). Zoneless change
  // detection only re-renders when something it actually watches changes: a signal write, an event
  // binding firing, an async pipe emitting. Writing a plain class property notifies nothing.
  //
  // That's fine while the only writer is the user typing, because ngModel's own change event kicks
  // change detection. It breaks the moment another component sets the value programmatically - which
  // is exactly what prefill() below does for the empty state's starter chips: the property changed
  // but the textarea never re-rendered. Signals notify on every write regardless of the caller, so
  // the two-way binding stays honest.
  readonly messageText = signal('');
  readonly workItemIdText = signal('');

  @ViewChild('field') private textarea?: ElementRef<HTMLTextAreaElement>;

  readonly attachedFile = signal<File | null>(null);
  readonly attachedPreviewUrl = signal<string | null>(null);
  readonly sending = signal(false);
  readonly errorMessage = signal<string | null>(null);

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) {
      this.setAttachedFile(file);
    }
    // Reset the <input> itself so selecting the SAME file again still fires a 'change' event -
    // browsers don't fire 'change' on a file input if the selected file(s) didn't change.
    input.value = '';
  }

  /** QA workflow nicety called out in the spec: pasting a screenshot straight from the clipboard
   * (e.g. after a Snipping Tool / Win+Shift+S capture) instead of having to save it to disk first and
   * then browse for it in a file picker. */
  onPaste(event: ClipboardEvent): void {
    const items = event.clipboardData?.items;
    if (!items) {
      return;
    }
    for (const item of items) {
      if (item.type.startsWith('image/')) {
        const file = item.getAsFile();
        if (file) {
          event.preventDefault();
          this.setAttachedFile(file);
        }
        return;
      }
    }
  }

  private setAttachedFile(file: File): void {
    this.revokePreviewUrl();
    this.attachedFile.set(file);
    // createObjectURL gives us a temporary browser-local URL that displays this in-memory File as an
    // <img> src without uploading it anywhere first - purely a local preview.
    this.attachedPreviewUrl.set(URL.createObjectURL(file));
  }

  removeAttachment(): void {
    this.revokePreviewUrl();
    this.attachedFile.set(null);
    this.workItemIdText.set('');
  }

  private revokePreviewUrl(): void {
    // Object URLs aren't automatically garbage-collected when you're done with them - each one holds
    // a reference to the underlying file data until explicitly revoked (or the page unloads). Not
    // revoking these in a long-lived chat session would leak memory a little more with every screenshot
    // attached/removed.
    const existing = this.attachedPreviewUrl();
    if (existing) {
      URL.revokeObjectURL(existing);
    }
  }

  ngOnDestroy(): void {
    // Mirror image of ngOnInit (see ado-settings.ts) - called once, right before Angular removes this
    // component (e.g. navigating to a different session). Without this, leaving the page with an
    // attachment still previewed would leak that object URL forever, since nothing else would ever
    // call revokePreviewUrl() for it.
    this.revokePreviewUrl();
  }

  /** Enter sends; Shift+Enter inserts a newline (standard chat-app convention) - letting the browser's
   * default textarea behavior happen for Shift+Enter, only intercepting the plain Enter case. Typed as
   * the base `Event` (not `KeyboardEvent`) because Angular's template type-checker only infers a plain
   * `Event` for the `(keydown.enter)` "key pseudo-event" binding syntax, not the more specific type. */
  onEnterKey(event: Event): void {
    if (!(event as KeyboardEvent).shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  send(): void {
    const file = this.attachedFile();
    if (file) {
      this.sendScreenshot(file);
    } else {
      this.sendText();
    }
  }

  /** Drops a starter phrase into the box and puts the cursor at the end, ready to finish. Called by
   * MessageThread's empty state through a @ViewChild reference to this component.
   *
   * Deliberately prefill-and-focus rather than send-immediately: most useful prompts here end in a
   * work item id that only the user knows ("what linked bugs are under #___"), so auto-sending would
   * fire off half a question. Every chip behaving the same way - text in the box, cursor ready - is
   * more predictable than some sending and some not. */
  prefill(prompt: string): void {
    if (this.sending()) {
      return;
    }
    this.messageText.set(prompt);

    const field = this.textarea?.nativeElement;
    if (field) {
      field.focus();
      // Angular writes the new value into the DOM after this tick, so move the caret afterwards -
      // otherwise setSelectionRange runs against the previous (empty) value and has no effect.
      queueMicrotask(() => field.setSelectionRange(prompt.length, prompt.length));
    }
  }

  private sendText(): void {
    const text = this.messageText().trim();
    if (!text || this.sending()) {
      return;
    }

    this.sending.set(true);
    this.errorMessage.set(null);
    this.awaitingReply.emit(true);
    // Clear the box the moment the message is on its way, not once the reply comes back - the model
    // can take several seconds to answer, and leaving the sent text sitting in the box reads as "did
    // this actually send?" rather than "Buddy is working on it." Restore it on failure below so a
    // dropped request doesn't cost the user their typed message.
    this.messageText.set('');

    this.chatService.sendMessage(this.sessionId(), text).subscribe({
      next: () => {
        this.sending.set(false);
        this.awaitingReply.emit(false);
        this.messageSent.emit();
      },
      error: () => {
        this.sending.set(false);
        this.awaitingReply.emit(false);
        this.messageText.set(text);
        this.errorMessage.set('Could not send that message. Please try again.');
      },
    });
  }

  private sendScreenshot(file: File): void {
    const workItemId = Number(this.workItemIdText());
    if (!Number.isInteger(workItemId) || workItemId <= 0) {
      this.errorMessage.set('Enter a valid work item ID to attach this screenshot to.');
      return;
    }
    if (this.sending()) {
      return;
    }

    this.sending.set(true);
    this.errorMessage.set(null);

    // ChatsController's [Required] on the `content` form field rejects an empty string outright
    // (confirmed against the real API: a screenshot-only send with no caption came back 400 "The
    // content field is required.") - a screenshot attached with no typed caption needs SOME text sent,
    // so we default to a placeholder rather than forcing the user to type something meaningless.
    const content = this.messageText().trim() || 'Screenshot attached.';

    this.chatService.appendScreenshotMessage(this.sessionId(), content, workItemId, file).subscribe({
      next: () => {
        // Note: a 200 response here does NOT necessarily mean the screenshot was successfully linked
        // in Azure DevOps - AppendMessageResult.success can be false (e.g. ADO rejected the request)
        // while the HTTP call itself still succeeds, because the backend always records a ChatMessage
        // either way (see AzureBuddy.Core/Chat/ChatSessionService.AppendMessageWithScreenshotAsync).
        // We don't need to branch on that here: whichever happened, the resulting message (a normal
        // success or an "I couldn't attach..." failure message) is already saved, and messageClassifier
        // will render it correctly (as 'screenshot' or 'error') once MessageThread reloads the list.
        this.sending.set(false);
        this.messageText.set('');
        this.removeAttachment();
        this.messageSent.emit();
      },
      error: (err: HttpErrorResponse) => {
        this.sending.set(false);
        // Both AdoNotConfiguredException (ChatsController's catch block) and a plain validation
        // failure (missing workItemId, oversized file) now use the same ApiErrorResponse shape - no
        // more guessing which kind of 400 this is by inspecting the body's type.
        this.errorMessage.set(extractApiErrorMessage(err.error, 'Could not upload that screenshot. Please try again.'));
      },
    });
  }
}
