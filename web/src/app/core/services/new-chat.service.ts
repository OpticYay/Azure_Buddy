import { Injectable, signal } from '@angular/core';

import { ChatSessionSummary } from '../models/chat.models';

/** The activity/composer key used for a conversation that doesn't have a real session id yet - see
 * ChatActivityService and message-composer.ts. Exported here rather than invented separately in each
 * file that needs it, since they all have to agree on the same string. */
export const DRAFT_SESSION_KEY = '__draft__';

/**
 * Coordinates the two things that happen around starting a conversation without ever persisting an
 * empty session row:
 *
 *   1. "New Conversation" no longer calls the backend - it just needs to tell the currently-mounted
 *      MessageThread/MessageComposer to drop whatever draft (typed text, attachment) is sitting in the
 *      box, even if the route doesn't change (you can already be looking at a blank/no-session view).
 *      `requestNewChat()` bumps a counter; anything that reads `resetToken()` inside an `effect()` re-runs
 *      when it changes, the same "signal write is the notification" pattern used everywhere else in this
 *      app (see ChatActivityService).
 *
 *   2. The session only actually gets created as a side effect of the first message being sent (see
 *      ChatController.PostAsync's null-SessionId path). Once that happens, MessageThread learns the new
 *      session's full details for free from the GET it was already going to do after navigating to the
 *      new URL, and hands them here so the sidebar can add the entry immediately instead of only picking
 *      it up on its next full reload.
 */
@Injectable({ providedIn: 'root' })
export class NewChatService {
  private readonly resetTokenSignal = signal(0);
  readonly resetToken = this.resetTokenSignal.asReadonly();

  private readonly createdSignal = signal<ChatSessionSummary | null>(null);
  readonly created = this.createdSignal.asReadonly();

  requestNewChat(): void {
    this.resetTokenSignal.update((value) => value + 1);
  }

  notifyCreated(session: ChatSessionSummary): void {
    this.createdSignal.set(session);
  }
}
