import {
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subscription, map, of, switchMap } from 'rxjs';

import { ChatService } from '../../../core/services/chat.service';
import { ChatActivityService } from '../../../core/services/chat-activity.service';
import { DRAFT_SESSION_KEY, NewChatService } from '../../../core/services/new-chat.service';
import { extractApiErrorMessage } from '../../../core/models/api-error.model';
import { popIn } from '../../../shared/animations';

// Emitted after every successful send (text or screenshot) so MessageThread knows to reload the
// message list - the composer's job ends at "the backend now has this message," not "update the
// thread's own state."
@Component({
  selector: 'app-message-composer',
  imports: [FormsModule],
  templateUrl: './message-composer.html',
  styleUrl: './message-composer.css',
  animations: [popIn],
})
export class MessageComposer implements OnDestroy {
  private readonly chatService = inject(ChatService);
  private readonly chatActivity = inject(ChatActivityService);
  private readonly newChatService = inject(NewChatService);

  /** Null means "this conversation hasn't sent a first message yet, so it has no session id" - see
   * new-chat.service.ts. Not required<string>() anymore: a brand-new, not-yet-persisted conversation is
   * a real, supported state here, not an error. */
  readonly sessionId = input<string | null>(null);

  /** Carries the session the message was sent TO, not whichever one is open when the reply lands -
   * a slow reply can settle after the user has already switched conversations, and the thread has to
   * be able to tell that it's being told about a session it's no longer showing. */
  readonly messageSent = output<string>();

  /** Fired only when sessionId() was null at send time and the backend just created a real session as
   * a side effect of that first message - see sendText()/sendScreenshot() below. MessageThread listens
   * for this to navigate to the new session's URL. Distinct from messageSent because the two need
   * different handling: messageSent means "reload THIS session," sessionCreated means "there is a new
   * session now and we need to move to it." */
  readonly sessionCreated = output<string>();

  /** Fired the moment a text send actually goes out, carrying the text itself - lets MessageThread
   * show the user's own message immediately instead of waiting for the round trip to finish and the
   * whole session to reload. Paired with messageFailed below for the one case that needs undoing. */
  readonly messageSubmitted = output<string>();

  /** Fired if the text send comes back as an error, so MessageThread can drop the optimistic message
   * it added on messageSubmitted - the text itself is already restored to the field by sendText()
   * below, so leaving it in the log too would show the same message twice. */
  readonly messageFailed = output<void>();

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
  /** Whether THIS composer's own session has a request in flight. Derived from the shared service
   * rather than held here, so a reply pending in another conversation no longer disables this one's
   * Send button - and so the state survives this component being destroyed and rebuilt. A draft
   * (sessionId() null) conversation doesn't have a real id to key by yet, so it uses the same fixed
   * DRAFT_SESSION_KEY every send() call below keys its own tracked request with. */
  readonly sending = computed(() =>
    this.chatActivity.isWorking(this.sessionId() ?? DRAFT_SESSION_KEY),
  );
  readonly errorMessage = signal<string | null>(null);

  /** The send currently in flight, if any - kept so stop() below has something to unsubscribe from.
   * Unsubscribing an RxJS Observable mid-request cancels the underlying HttpClient call (aborts the
   * fetch/XHR), and chat-activity.service's finalize() still runs on unsubscribe, so sending() clears
   * itself the same way it would for a normal success or error. See §7.7. */
  private inFlightSubscription: Subscription | null = null;

  constructor() {
    // Third piece of state that outlived the conversation it belonged to (see the working indicator
    // and pendingMessage): this component is reused across session switches, so "Could not send that
    // message" from one conversation stayed pinned above the composer in the next one. Reading
    // sessionId() is what subscribes this effect to it.
    effect(() => {
      this.sessionId();
      this.errorMessage.set(null);
    });

    // "New Conversation" was clicked - clear whatever was mid-draft here (typed text, an attached
    // screenshot) so it doesn't leak into the blank conversation the click just asked for. This has to
    // be a separate effect from the one above: the route can stay at sessionId() === null across
    // repeated clicks (there's nothing to navigate TO until a message is actually sent), so a plain
    // sessionId() dependency alone would never re-fire on the second, third, ... click. resetToken()
    // bumps on every click regardless of whether the id changed.
    //
    // The reset body runs inside untracked() because an effect subscribes to every signal it READS,
    // not just the one it means to watch - and removeAttachment() reads attachedPreviewUrl (to revoke
    // the object URL). That made this effect depend on attachedPreviewUrl, which setAttachedFile()
    // writes: attaching or pasting a screenshot re-fired this very effect, which then cleared the
    // attachment (and the typed caption) the instant it was added, so a screenshot could never be
    // sent at all - send() found no file and fell through to sendText(). untracked() runs the same
    // code without registering any of it as a dependency, leaving resetToken() as the sole trigger.
    effect(() => {
      this.newChatService.resetToken();
      untracked(() => {
        this.messageText.set('');
        this.removeAttachment();
        this.errorMessage.set(null);
        const field = this.textarea?.nativeElement;
        if (field) {
          field.style.height = 'auto';
        }
      });
    });
  }

  /** Grows the textarea to fit its content as the user types, up to the CSS `max-height` (140px, see
   * message-composer.css) where `overflow-y: auto` takes back over - a plain `rows="1"` left a long
   * multi-line draft scrolling inside one visible row instead of growing with it (§7.9). Resetting
   * height to 'auto' first is what lets scrollHeight shrink back down again after deleting text; without
   * it scrollHeight only ever reports the tallest the box has ever been. */
  autoGrow(event: Event): void {
    const field = event.target as HTMLTextAreaElement;
    field.style.height = 'auto';
    field.style.height = `${field.scrollHeight}px`;
  }

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
    //
    // Read untracked so this cleanup can never subscribe a caller (the reset effect above calls it)
    // to the same signal it then clears - a read-then-write of one signal inside an effect is exactly
    // the self-retriggering loop this method used to cause.
    const existing = untracked(this.attachedPreviewUrl);
    if (existing) {
      URL.revokeObjectURL(existing);
    }
    // Clear the signal too, not just the browser-side URL: leaving the revoked string in place left
    // attachedPreviewUrl reporting a dead URL after every removeAttachment().
    this.attachedPreviewUrl.set(null);
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

    // Captured now rather than read again in the callbacks: the user can switch conversations while
    // this request is in flight, at which point sessionId() reports the NEW session. Every use below
    // has to stay pinned to the session (or draft) the message was actually sent from. null here means
    // this is the first message of a brand-new conversation - nothing has been persisted yet.
    const sessionId = this.sessionId();
    const activityKey = sessionId ?? DRAFT_SESSION_KEY;

    this.errorMessage.set(null);
    // Clear the box the moment the message is on its way, not once the reply comes back - the model
    // can take several seconds to answer, and leaving the sent text sitting in the box reads as "did
    // this actually send?" rather than "Buddy is working on it." Restore it on failure below so a
    // dropped request doesn't cost the user their typed message.
    this.messageText.set('');
    this.messageSubmitted.emit(text);

    // chatService.sendMessage(null, text) is the single call that both creates the session and records
    // this first message - see ChatController.PostAsync. There is no separate "create session" request
    // for the frontend to make (and no window where an empty session would exist because of one).
    // track() marks the request busy and clears it via finalize() when it settles, however it settles -
    // see chat-activity.service.ts.
    this.inFlightSubscription = this.chatActivity
      .track(activityKey, this.chatService.sendMessage(sessionId, text))
      .subscribe({
        next: (response) => {
          this.inFlightSubscription = null;
          if (sessionId) {
            this.messageSent.emit(sessionId);
          } else {
            // The backend just created a real session for this. MessageThread needs to navigate to it -
            // it can't just reload "this" session, because there was no session to reload.
            this.sessionCreated.emit(response.sessionId);
          }
          this.focusField();
        },
        error: () => {
          this.inFlightSubscription = null;
          this.messageText.set(text);
          this.messageFailed.emit();
          this.errorMessage.set('Could not send that message. Please try again.');
          this.focusField();
        },
      });
  }

  /** Aborts the in-flight send - see inFlightSubscription's doc comment for how unsubscribing does
   * that. There's deliberately no separate "cancelled" state to clean up: unsubscribing runs
   * chat-activity's finalize() exactly like a normal completion, so sending() flips back to false on
   * its own. The one thing that IS cancellation-specific is restoring the optimistic UI a normal
   * error would have restored too - the typed text and the pending bubble in the log - since neither
   * the success nor the error handler above gets to run for an aborted request. */
  stop(): void {
    this.inFlightSubscription?.unsubscribe();
    this.inFlightSubscription = null;
    this.messageFailed.emit();
    this.errorMessage.set('Send cancelled.');
    this.focusField();
  }

  private focusField(): void {
    const field = this.textarea?.nativeElement;
    if (field) {
      // Matches the queueMicrotask pattern prefill() uses below - the field is already in the DOM
      // here (unlike prefill's case), but queueing keeps the two focus-management code paths
      // consistent and avoids fighting Angular's own change-detection timing on the disabled state.
      queueMicrotask(() => field.focus());
    }
    // autoGrow() only runs on the (input) event, so a programmatic clear (messageText.set('') below)
    // leaves the inline height it set behind - the box would stay tall around empty text otherwise.
    if (field) {
      field.style.height = 'auto';
    }
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

    const existingSessionId = this.sessionId();
    const activityKey = existingSessionId ?? DRAFT_SESSION_KEY;
    this.errorMessage.set(null);

    // A screenshot attached with no typed caption is a normal send, so it gets a placeholder rather
    // than the user being made to type something meaningless. ChatsController applies the same default
    // server-side (`content` is deliberately not [Required] there anymore, precisely so a caption-less
    // screenshot isn't a 400) - this one just keeps the text the same whichever side supplies it.
    const content = this.messageText().trim() || 'Screenshot attached.';

    // Unlike sendText() above, POST /api/chats/{id}/messages has no "create the session if there isn't
    // one yet" mode - it always needs a real id. So a screenshot sent as the very first message of a
    // brand-new conversation still needs an explicit create-then-append: create an (empty, for a moment)
    // session, then upload into it. That moment is exactly what ListSessionsAsync's zero-message filter
    // and the /api/chats/empty cleanup endpoint exist to cover, in case the upload never completes (a
    // dropped connection between the two calls, etc.) - see ChatSessionService for both.
    const upload$: Observable<{ sessionId: string }> = (
      existingSessionId
        ? of(existingSessionId)
        : this.chatService.createSession(null).pipe(map((session) => session.id))
    ).pipe(
      switchMap((sessionId) =>
        this.chatService
          .appendScreenshotMessage(sessionId, content, workItemId, file)
          .pipe(map(() => ({ sessionId }))),
      ),
    );

    this.inFlightSubscription = this.chatActivity.track(activityKey, upload$).subscribe({
      next: ({ sessionId }) => {
        this.inFlightSubscription = null;
        // Note: a successful upload$ here does NOT necessarily mean the screenshot was successfully
        // linked in Azure DevOps - AppendMessageResult.success can be false (e.g. ADO rejected the
        // request) while the HTTP call itself still succeeds, because the backend always records a
        // ChatMessage either way (see ChatSessionService.AppendMessageWithScreenshotAsync). We don't
        // need to branch on that here: whichever happened, the resulting message (a normal success or
        // an "I couldn't attach..." failure message) is already saved, and messageClassifier will
        // render it correctly (as 'screenshot' or 'error') once the thread reloads.
        this.messageText.set('');
        this.removeAttachment();
        if (existingSessionId) {
          this.messageSent.emit(sessionId);
        } else {
          this.sessionCreated.emit(sessionId);
        }
        this.focusField();
      },
      error: (err: HttpErrorResponse) => {
        this.inFlightSubscription = null;
        // Both AdoNotConfiguredException (ChatsController's catch block) and a plain validation
        // failure (missing workItemId, oversized file) now use the same ApiErrorResponse shape - no
        // more guessing which kind of 400 this is by inspecting the body's type.
        this.errorMessage.set(
          extractApiErrorMessage(err.error, 'Could not upload that screenshot. Please try again.'),
        );
        this.focusField();
      },
    });
  }
}
