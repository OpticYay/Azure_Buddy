import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';

import { SessionList } from '../session-list/session-list';
import { MessageThread } from '../message-thread/message-thread';

// The SAME component instance is reused across navigations between /chat and /chat/:sessionId (it
// isn't destroyed and recreated), so sessionId needs to be read reactively rather than once.
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
