import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';

import { SessionList } from '../session-list/session-list';
import { MessageThread } from '../message-thread/message-thread';

// ── Bridging RxJS and signals ────────────────────────────────────────────────────────────────────
// The Angular Router exposes route parameters (like :sessionId in "/chat/:sessionId") as an
// Observable, because the SAME component instance is reused across navigations between /chat and
// /chat/:sessionId - it doesn't get destroyed and recreated, so it needs some way to be notified when
// just the parameter changes. `toSignal(...)` converts that Observable into a signal, so the rest of
// this component (and its template) can read `sessionId()` like any other signal instead of having to
// `.subscribe()` and manually manage that subscription's lifecycle (remembering to unsubscribe when
// the component is destroyed, etc. - toSignal handles that automatically).
@Component({
  selector: 'app-chat-page',
  imports: [SessionList, MessageThread],
  templateUrl: './chat-page.html',
  styleUrl: './chat-page.css',
})
export class ChatPage {
  private readonly route = inject(ActivatedRoute);

  readonly sessionId = toSignal(
    this.route.paramMap.pipe(map((params) => params.get('sessionId'))),
    { initialValue: null },
  );
}
