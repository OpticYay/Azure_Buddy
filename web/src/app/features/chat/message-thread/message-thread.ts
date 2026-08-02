import { Component, ElementRef, ViewChild, computed, effect, inject, input, signal } from '@angular/core';

import { ChatService } from '../../../core/services/chat.service';
import { AdoSettingsService } from '../../../core/services/ado-settings.service';
import { ChatMessageView } from '../../../core/models/chat.models';
import { classifyMessage } from '../../../core/services/message-classifier';
import { MessageItem } from '../message-item/message-item';
import { MessageComposer } from '../message-composer/message-composer';
import { TypingIndicator } from '../../../shared/ui/typing-indicator/typing-indicator';
import { crossFade, fadeSlideIn, openerStagger } from '../../../shared/animations';

/** Shown on an empty conversation. These aren't decoration - the deterministic flows behind this
 * chat (list my items, view linked bugs, file a bug) aren't discoverable any other way, so the
 * empty state is where you learn what the tool can actually do. Phrased the way a QA engineer would
 * actually ask, so they double as examples of what wording works. */
const STARTERS = [
  'Show my open work items',
  'What linked bugs are under #',
  'File a bug against ',
];

// ── What is effect(), and how is it different from computed()? ─────────────────────────────────────
// `computed()` (used back in AuthService for `currentUser`) DERIVES a new signal value from other
// signals - it's for producing a value to read. `effect()` instead RUNS A SIDE EFFECT whenever any
// signal it reads changes - it produces no value, it just does something (here: fetch new data from
// the server). We need an effect (not just ngOnInit) because this component's `sessionId` INPUT can
// change without the component itself being destroyed and recreated - see chat-page.ts: ChatPage stays
// mounted across /chat/:id1 -> /chat/:id2 navigations (only the route parameter changes), so
// MessageThread stays mounted too, and ngOnInit (which only ever runs once) would never notice the
// user switched sessions. An effect re-runs automatically every time `this.sessionId()` changes.
@Component({
  selector: 'app-message-thread',
  imports: [MessageItem, MessageComposer, TypingIndicator],
  templateUrl: './message-thread.html',
  styleUrl: './message-thread.css',
  animations: [crossFade, fadeSlideIn, openerStagger],
})
export class MessageThread {
  private readonly chatService = inject(ChatService);
  private readonly adoSettingsService = inject(AdoSettingsService);

  readonly sessionId = input.required<string>();

  private readonly messages = signal<ChatMessageView[]>([]);

  /** The user's own message, shown the instant it's sent rather than waiting for the round trip to
   * finish and the whole session to reload (see onMessageSubmitted below) - cleared once that reload
   * brings back the real, persisted version, or if the send actually failed. */
  private readonly pendingMessage = signal<ChatMessageView | null>(null);

  readonly displayMessages = computed(() => {
    const pending = this.pendingMessage();
    const all = pending ? [...this.messages(), pending] : this.messages();
    return all.map(classifyMessage);
  });

  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  /** Driven by MessageComposer's (awaitingReply) output - true while a text message's POST /chat call
   * is in flight, so the typing indicator shows exactly during that window. */
  readonly isAwaitingReply = signal(false);

  /** Built once from the user's saved ADO settings (see AdoSettingsService) and handed down to every
   * MessageItem so it can render real, clickable "open in Azure DevOps" links for work item ids -
   * null until that loads, or if the user hasn't configured ADO settings yet (message-item.ts falls
   * back to plain, unlinked "#123" text in that case rather than a broken link). */
  readonly adoWorkItemBaseUrl = signal<string | null>(null);

  @ViewChild('scrollAnchor') private scrollAnchor?: ElementRef<HTMLDivElement>;

  /** A reference to the child composer component instance (not its DOM element) - that's what lets
   * the empty state's starter chips drop text into the composer's own field. */
  @ViewChild(MessageComposer) private composer?: MessageComposer;

  readonly starters = STARTERS;

  useStarter(prompt: string): void {
    this.composer?.prefill(prompt);
  }

  constructor() {
    effect(() => {
      const id = this.sessionId();
      this.loadMessages(id);
    });

    this.adoSettingsService.get().subscribe((settings) => {
      if (settings.isConfigured && settings.organizationUrl && settings.defaultProject) {
        this.adoWorkItemBaseUrl.set(
          `${settings.organizationUrl.replace(/\/$/, '')}/${settings.defaultProject}/_workitems/edit`,
        );
      }
    });
  }

  private loadMessages(sessionId: string): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.chatService.getSession(sessionId).subscribe({
      next: (detail) => {
        this.messages.set(detail.messages);
        this.pendingMessage.set(null);
        this.loading.set(false);
        this.scrollToBottom();
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Could not load this conversation.');
      },
    });
  }

  /** Called via the (messageSent) output binding once MessageComposer confirms the backend has
   * recorded a new message - see message-composer.ts's `messageSent.emit()`. We simply re-fetch the
   * whole session rather than trying to append the new message locally: /chat's response is just
   * {sessionId, reply} (no id/timestamp for the persisted messages), so re-fetching is the simplest
   * way to get the authoritative, fully-populated message list back - acceptable for a QA-internal
   * tool's message volume, though a high-traffic chat app would want to append optimistically instead. */
  onMessageSent(): void {
    this.loadMessages(this.sessionId());
  }

  /** Builds a throwaway ChatMessageView for the pending bubble - never sent anywhere, just enough shape
   * for classifyMessage/MessageItem to render it exactly like a real user message. Its id only needs to
   * be unique for @for's `track`, which is why Date.now() (not a real backend id) is good enough. */
  onMessageSubmitted(text: string): void {
    this.pendingMessage.set({
      id: `pending-${Date.now()}`,
      role: 'User',
      content: text,
      adoAttachmentUrl: null,
      workItemId: null,
      type: 'Text',
      table: null,
      createdAt: new Date().toISOString(),
    });
    this.scrollToBottom();
  }

  onMessageFailed(): void {
    this.pendingMessage.set(null);
  }

  onAwaitingReplyChange(awaiting: boolean): void {
    this.isAwaitingReply.set(awaiting);
    if (awaiting) {
      this.scrollToBottom();
    }
  }

  private scrollToBottom(): void {
    // Wait a tick for Angular to actually render the new messages into the DOM before trying to
    // scroll to an element that (from the browser's perspective) doesn't exist yet.
    queueMicrotask(() => {
      this.scrollAnchor?.nativeElement.scrollIntoView({ behavior: 'smooth' });
    });
  }
}
