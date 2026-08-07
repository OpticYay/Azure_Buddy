import { Injectable, signal } from '@angular/core';
import { Observable, finalize } from 'rxjs';

/**
 * Tracks which chat sessions currently have a request in flight, so the "Working" indicator and the
 * composer's Send button can reflect the state of ONE conversation rather than the app as a whole.
 *
 * ── Why this isn't component state ──────────────────────────────────────────────────────────────
 * It used to be: a `sending` boolean on MessageComposer and an `isAwaitingReply` boolean on
 * MessageThread. Both components stay mounted while you switch between conversations (ChatPage only
 * swaps a route parameter - see message-thread.ts), so those flags followed you into whichever
 * conversation you opened next: a reply still pending in session A left session B showing "Working"
 * with its Send button disabled. And both components ARE destroyed when you leave /chat for the
 * Connection or Model page, so coming back reset them to false and the indicator vanished while the
 * request was still genuinely running. One flag, wrong scope in both directions.
 *
 * Keying by session id fixes the first; living in a root-provided service - created once and never
 * torn down with any component - fixes the second.
 */
@Injectable({ providedIn: 'root' })
export class ChatActivityService {
  // A new Set on every write rather than mutating in place: signals compare by reference, so mutating
  // the existing Set would change what `has()` reports without ever notifying anything watching it.
  private readonly inFlight = signal<ReadonlySet<string>>(new Set<string>());

  /** Read this inside a computed()/template and it stays reactive - the signal is read on every call. */
  isWorking(sessionId: string): boolean {
    return this.inFlight().has(sessionId);
  }

  /**
   * Marks `sessionId` busy for exactly as long as `source` is running, then clears it - whether the
   * request succeeded, failed, or was cancelled by the caller unsubscribing.
   *
   * ── What finalize() does ────────────────────────────────────────────────────────────────────────
   * finalize() registers a callback that RxJS runs when the observable stops producing values, for
   * any reason: a successful completion, an error, or an unsubscribe. It's the observable equivalent
   * of a `finally` block.
   *
   * That matters because the obvious way to write this - set the flag true before the call, set it
   * false in the success handler - only covers the happy path. An error (the model timing out, the
   * network dropping, a 500) skips the success handler entirely, so the flag is never cleared and the
   * button stays disabled on "Sending" forever, with no way back except a page reload. Writing the
   * reset in BOTH the success and error handlers fixes those two cases but still misses cancellation,
   * and duplicates the cleanup. finalize() is one callback that covers all of them.
   */
  track<T>(sessionId: string, source: Observable<T>): Observable<T> {
    this.mark(sessionId, true);
    return source.pipe(finalize(() => this.mark(sessionId, false)));
  }

  private mark(sessionId: string, working: boolean): void {
    const next = new Set(this.inFlight());
    if (working) {
      next.add(sessionId);
    } else {
      next.delete(sessionId);
    }
    this.inFlight.set(next);
  }
}
