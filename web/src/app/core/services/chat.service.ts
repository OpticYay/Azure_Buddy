import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, timeout } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  AppendMessageResult,
  ChatResponse,
  ChatSessionDetail,
  ChatSessionSummary,
  CreateSessionRequest,
  PagedResult,
  RenameSessionRequest,
} from '../models/chat.models';

const CHATS_BASE_URL = `${environment.apiUrl}/api/chats`;
const LIVE_CHAT_URL = `${environment.apiUrl}/api/chat`;

/** Ceiling on how long the frontend waits for a chat send to settle before giving up on it itself,
 * rather than relying solely on the backend's own timeout - see §7.7. A hung request otherwise left
 * the composer disabled indefinitely, with no client-side path back to a usable state. The agent
 * flow this routes through can genuinely take a while (multiple ADO calls, an LLM round trip), so
 * this is generous rather than tight. */
const CHAT_REQUEST_TIMEOUT_MS = 45_000;

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);

  listSessions(page: number, pageSize: number): Observable<PagedResult<ChatSessionSummary>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<ChatSessionSummary>>(CHATS_BASE_URL, { params });
  }

  getSession(sessionId: string): Observable<ChatSessionDetail> {
    return this.http.get<ChatSessionDetail>(`${CHATS_BASE_URL}/${sessionId}`);
  }

  createSession(title: string | null): Observable<ChatSessionDetail> {
    const request: CreateSessionRequest = { title };
    return this.http.post<ChatSessionDetail>(CHATS_BASE_URL, request);
  }

  deleteSession(sessionId: string): Observable<void> {
    return this.http.delete<void>(`${CHATS_BASE_URL}/${sessionId}`);
  }

  renameSession(sessionId: string, title: string): Observable<ChatSessionSummary> {
    const request: RenameSessionRequest = { title };
    return this.http.put<ChatSessionSummary>(`${CHATS_BASE_URL}/${sessionId}/title`, request);
  }

  /** The "smart" path: POST /chat routes the message through the backend's deterministic flows /
   * conversational agent (see IntentRouter on the backend) and returns its reply. The backend persists
   * BOTH the user's message and the reply itself - this call doesn't need a separate "save my message"
   * step, unlike the screenshot path below. Text-only messages always go through here, never through
   * the multipart endpoint (that one exists specifically for the screenshot case - see ChatsController).
   *
   * `sessionId: null` is how a brand-new, not-yet-persisted conversation sends its first message:
   * ChatController.PostAsync creates the session AND appends this message in the same request, so there
   * is never a moment where an empty session exists as a separate round trip the frontend has to manage -
   * see message-composer.ts's sendText() and the wider "New Conversation" fix. */
  sendMessage(sessionId: string | null, message: string): Observable<ChatResponse> {
    return this.http
      .post<ChatResponse>(LIVE_CHAT_URL, { sessionId, message })
      .pipe(timeout(CHAT_REQUEST_TIMEOUT_MS));
  }

  /** The screenshot path: POST /api/chats/{id}/messages as multipart/form-data (a plain JSON body
   * can't carry binary file data). Unlike sendMessage above, this does NOT invoke the agent/routing -
   * it just records the message and uploads+links the screenshot to the given ADO work item; no
   * assistant reply is generated for it. `FormData` is the browser API for building a multipart body -
   * conceptually a set of key/value pairs where a value can be a file, mirroring exactly what
   * ChatsController.AppendMessageAsync expects as [FromForm] parameters. */
  appendScreenshotMessage(
    sessionId: string,
    content: string,
    workItemId: number,
    screenshot: File,
  ): Observable<AppendMessageResult> {
    const formData = new FormData();
    formData.append('role', 'User');
    formData.append('content', content);
    formData.append('workItemId', String(workItemId));
    formData.append('screenshot', screenshot, screenshot.name);

    return this.http
      .post<AppendMessageResult>(`${CHATS_BASE_URL}/${sessionId}/messages`, formData)
      .pipe(timeout(CHAT_REQUEST_TIMEOUT_MS));
  }

  /** Uploads a file into the session's single pending-attachment slot ahead of a normal sendMessage()
   * call - the agent picks it up via its attach_file_to_work_item tool once the user names (or is asked
   * to name) a work item in the chat turn that follows. Unlike appendScreenshotMessage above, this never
   * writes a chat message itself and never needs a workItemId up front - see ChatsController's
   * UploadAttachmentAsync and IPendingAttachmentStore. */
  uploadPendingAttachment(
    sessionId: string,
    file: File,
  ): Observable<{ fileName: string; contentType: string; size: number }> {
    const formData = new FormData();
    formData.append('file', file, file.name);

    return this.http
      .post<{ fileName: string; contentType: string; size: number }>(
        `${CHATS_BASE_URL}/${sessionId}/attachments`,
        formData,
      )
      .pipe(timeout(CHAT_REQUEST_TIMEOUT_MS));
  }
}
