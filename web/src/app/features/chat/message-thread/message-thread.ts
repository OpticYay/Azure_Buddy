import {
  Component,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';

import { ChatService } from '../../../core/services/chat.service';
import { ChatActivityService } from '../../../core/services/chat-activity.service';
import { DRAFT_SESSION_KEY, NewChatService } from '../../../core/services/new-chat.service';
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
const STARTERS = ['Show my open work items', 'What linked bugs are under #', 'File a bug against '];

// Uses effect(), not ngOnInit, because this component's `sessionId` input can change without the
// component itself being destroyed and recreated (ChatPage stays mounted across /chat/:id1 ->
// /chat/:id2 navigations), and ngOnInit only ever runs once.
@Component({
  selector: 'app-message-thread',
  imports: [MessageItem, MessageComposer, TypingIndicator],
  templateUrl: './message-thread.html',
  styleUrl: './message-thread.css',
  animations: [crossFade, fadeSlideIn, openerStagger],
})
export class MessageThread {
  private readonly chatService = inject(ChatService);
  private readonly chatActivity = inject(ChatActivityService);
  private readonly newChatService = inject(NewChatService);
  private readonly adoSettingsService = inject(AdoSettingsService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  /** Null on the bare /chat route: "no conversation open yet" - not an error state, the composer still
   * renders (see the template) and is ready to start a brand-new one. See ChatPage/chat-page.html,
   * which now always mounts this component instead of only doing so once a session id exists. */
  readonly sessionId = input<string | null>(null);

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

  /** True while THIS session has a request in flight, so the typing indicator shows in the
   * conversation the message was actually sent to and nowhere else. Read from the shared service
   * rather than tracked here, so it neither leaks into the next conversation you open nor resets to
   * false just because you visited the Connection page and came back. */
  readonly isAwaitingReply = computed(() =>
    this.chatActivity.isWorking(this.sessionId() ?? DRAFT_SESSION_KEY),
  );

  /** Built once from the user's saved ADO settings (see AdoSettingsService) and handed down to every
   * MessageItem so it can render real, clickable "open in Azure DevOps" links for work item ids -
   * null until that loads, or if the user hasn't configured ADO settings yet (message-item.ts falls
   * back to plain, unlinked "#123" text in that case rather than a broken link). */
  readonly adoWorkItemBaseUrl = signal<string | null>(null);

  @ViewChild('logContainer') private logContainer?: ElementRef<HTMLDivElement>;

  /** How close to the bottom (in pixels) still counts as "at the bottom" for auto-scroll purposes -
   * a reader who has scrolled up even a little is deliberately reading history, but a few leftover
   * pixels from a still-settling layout shouldn't count as having scrolled away. */
  private static readonly NEAR_BOTTOM_THRESHOLD_PX = 48;

  /** Whether new content should show a "jump to latest" affordance instead of being auto-scrolled to -
   * see scrollToBottom() and onLogScroll() below. */
  readonly hasUnseenMessages = signal(false);
  private isNearBottom = true;

  /** Tracks how close to the bottom the reader currently is, so a reply that lands while they've
   * scrolled up to read earlier history doesn't yank them back down without warning (see §7.6). */
  onLogScroll(): void {
    const el = this.logContainer?.nativeElement;
    if (!el) {
      return;
    }
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    this.isNearBottom = distanceFromBottom <= MessageThread.NEAR_BOTTOM_THRESHOLD_PX;
    if (this.isNearBottom) {
      this.hasUnseenMessages.set(false);
    }
  }

  jumpToLatest(): void {
    this.hasUnseenMessages.set(false);
    this.scrollToBottom(true);
  }

  /** A reference to the child composer component instance (not its DOM element) - that's what lets
   * the empty state's starter chips drop text into the composer's own field. */
  @ViewChild(MessageComposer) private composer?: MessageComposer;

  readonly starters = STARTERS;

  useStarter(prompt: string): void {
    this.composer?.prefill(prompt);
  }

  /** Set right before navigating from a draft to the session the composer just created, so the next
   * loadMessages() call knows to also tell the sidebar about it - see onSessionCreated and
   * loadMessages' success handler below. */
  private readonly pendingNewSessionId = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.sessionId();
      // Re-run this effect even when `id` itself hasn't changed (e.g. "New Conversation" clicked again
      // while already on the bare /chat route) - reading resetToken() is what makes that happen.
      this.newChatService.resetToken();
      // Same "state that outlived its session" bug the working indicator had: this component is reused
      // across session switches, so an optimistic message added to session A stayed on screen and got
      // rendered into session B's log until B's fetch came back and replaced it. Dropped synchronously
      // here rather than waiting for loadMessages' response, which is exactly the window it was visible in.
      this.pendingMessage.set(null);
      // Opening a (different) conversation always starts pinned to its latest message, regardless of
      // where the reader happened to have scrolled the previous one.
      this.isNearBottom = true;
      this.hasUnseenMessages.set(false);

      if (id) {
        this.loadMessages(id, true);
      } else {
        // A draft conversation has nothing to fetch - GET /api/chats/{id} doesn't apply until a real
        // session exists. Show the same empty/opener state a freshly-created, still-empty session would.
        this.messages.set([]);
        this.loading.set(false);
        this.loadError.set(null);
      }
    });

    // The typing indicator appearing/disappearing is itself new content in the log - without this,
    // scrolling only followed messages, so a reply finishing (indicator disappears, real content
    // appears via onMessageSent -> loadMessages) was the one moment scrolling was inconsistent with
    // the rest of the log's behaviour. Non-forced: still respects isNearBottom like any other
    // reply-driven update.
    effect(() => {
      this.isAwaitingReply();
      this.scrollToBottom();
    });

    this.adoSettingsService.get().subscribe((settings) => {
      if (settings.isConfigured && settings.organizationUrl && settings.defaultProject) {
        this.adoWorkItemBaseUrl.set(
          `${settings.organizationUrl.replace(/\/$/, '')}/${settings.defaultProject}/_workitems/edit`,
        );
      }
    });
  }

  /** `forceScroll` is true for the initial open of a conversation (always land at the bottom) and
   * false for a reload triggered by a reply landing in the background - see scrollToBottom() and
   * §7.6 for why those two cases need to behave differently. */
  private loadMessages(sessionId: string, forceScroll = false): void {
    this.loading.set(true);
    this.loadError.set(null);

    this.chatService.getSession(sessionId).subscribe({
      next: (detail) => {
        this.messages.set(detail.messages);
        this.pendingMessage.set(null);
        this.loading.set(false);
        this.scrollToBottom(forceScroll);

        // This GET already has everything the sidebar needs (title, timestamps) to show the entry that
        // sending the first message just created - reusing it here means the sidebar updates without a
        // second network call. Only fires for the one load that follows onSessionCreated's navigation,
        // not on every ordinary open of an existing session (which would wrongly bump it to the top of
        // a list ordered by UpdatedAt, since opening a session doesn't change UpdatedAt).
        if (this.pendingNewSessionId() === sessionId) {
          this.pendingNewSessionId.set(null);
          this.newChatService.notifyCreated({
            id: detail.id,
            title: detail.title,
            createdAt: detail.createdAt,
            updatedAt: detail.updatedAt,
          });
        }
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
  onMessageSent(sessionId: string): void {
    // A reply can land after the user has already opened a different conversation. Reloading
    // `this.sessionId()` on any completion meant a slow reply in session A triggered a redundant
    // refetch of whichever session was on screen, so guard on the id the composer actually sent to.
    if (sessionId !== this.sessionId()) {
      return;
    }
    this.loadMessages(sessionId);
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
    // Always scroll for the reader's own message, even if they'd scrolled up to read history first -
    // sending a message is a clear "I'm done reading, back to the bottom" signal.
    this.scrollToBottom(true);
  }

  onMessageFailed(): void {
    this.pendingMessage.set(null);
  }

  /** The composer's sessionId() was null and it just sent the first message of a brand-new
   * conversation - the backend created a real session as a side effect of that single call (see
   * ChatService.sendMessage's doc comment). Navigate there: the route change updates this component's
   * own `sessionId` input, which re-runs the constructor's effect and loads the new session for real. */
  onSessionCreated(newSessionId: string): void {
    this.pendingNewSessionId.set(newSessionId);
    this.router.navigate(['/chat', newSessionId]);
  }

  /** `force` bypasses the near-bottom check - used for the reader's own actions (an initial load, or
   * their own message appearing) where jumping to the bottom is always what's wanted. A reply landing
   * while the reader has scrolled up to read earlier history is the one case that respects
   * `isNearBottom` instead: see §7.6 - it shows the "jump to latest" affordance rather than yanking
   * their scroll position out from under them. */
  private scrollToBottom(force = false): void {
    if (!force && !this.isNearBottom) {
      this.hasUnseenMessages.set(true);
      return;
    }

    // afterNextRender (not queueMicrotask, which this used to be) is the one API that's actually
    // guaranteed to run AFTER Angular has painted the change to the DOM. queueMicrotask just races
    // Angular's own zoneless rendering scheduler - which also runs via a microtask - with no
    // guarantee ours goes second. Losing that race meant we'd measure/scroll against the OLD layout
    // (the container had just been torn down by `loading()` flipping true then false around the
    // reload), so the browser's default "new content resets scrollTop to 0" behavior is what actually
    // won, and the log was left sitting at the top instead of following the new message down.
    //
    // Setting scrollTop directly (not scrollAnchor.scrollIntoView({behavior:'smooth'}), which this
    // used to be) instead of a smooth animated scroll: a long reply's text can still be reflowing
    // for a moment after this fires, and an in-progress smooth scroll doesn't re-target itself as
    // that happens - it was landing short of the true bottom. An instant jump has no such window.
    afterNextRender(
      () => {
        const el = this.logContainer?.nativeElement;
        if (el) {
          el.scrollTop = el.scrollHeight;
          this.isNearBottom = true;
          this.hasUnseenMessages.set(false);
        }
      },
      { injector: this.injector },
    );
  }
}
