// Mirrors AzureBuddy.Core/Chat/ChatModels.cs.

// The backend now serializes every enum as its member NAME ("User"/"Assistant"), not the underlying
// number (see Program.cs's AddJsonOptions registering a JsonStringEnumConverter globally) - so these
// are plain string-literal union types, not a number-mapping const object like this used to be. A
// union of string literals (`'User' | 'Assistant'`) is TypeScript's way of saying "this value must be
// exactly one of these specific strings" - narrower and more self-describing than plain `string`.
export type ChatMessageRoleValue = 'User' | 'Assistant';

/** Mirrors AzureBuddy.Data.Entities.ChatMessageType - what KIND of content a message's `content`
 * string represents, tagged by the backend at creation time (see FlowResult.cs) instead of the
 * frontend having to guess from the text (which is what message-classifier.ts used to do). */
export type ChatMessageTypeValue = 'Text' | 'Table' | 'Confirmation' | 'Error';

/** Structured payload for a 'Table'-typed message - present only when type === 'Table'. */
export interface ChatMessageTableData {
  headers: string[];
  rows: string[][];
}

export interface ChatSessionSummary {
  id: string; // Guid -> string over JSON
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface ChatMessageView {
  id: string;
  role: ChatMessageRoleValue;
  content: string;
  adoAttachmentUrl: string | null;
  workItemId: number | null;
  type: ChatMessageTypeValue;
  table: ChatMessageTableData | null;
  createdAt: string;
}

export interface ChatSessionDetail {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ChatMessageView[];
}

/** Generic paged wrapper - matches PagedResult<T> on the backend for any T. `hasMore` is computed
 * server-side now (Page/PageSize/TotalCount -> "is there another page?") instead of every consumer
 * (this app's SessionList included) having to recompute the same arithmetic itself. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
}

export interface CreateSessionRequest {
  title: string | null;
}

export interface RenameSessionRequest {
  title: string;
}

/** Response shape of POST /api/chats/{id}/messages *when a screenshot was attached* - the endpoint
 * returns a bare ChatMessageView when there's no screenshot, but this richer shape when there is
 * (see ChatsController.AppendMessageAsync and AppendMessageResult on the backend). */
export interface AppendMessageResult {
  success: boolean;
  message: ChatMessageView;
  error: string | null;
}

/** Response shape of POST /chat (the live agent turn) - see ChatController.ChatResponse. Now carries
 * the same type/workItemId/table tagging a persisted ChatMessageView does, so a table or confirmation
 * reply can render richly immediately without a follow-up GET /api/chats/{id}. */
export interface ChatResponse {
  sessionId: string;
  reply: string;
  type: ChatMessageTypeValue;
  workItemId: number | null;
  table: ChatMessageTableData | null;
}
