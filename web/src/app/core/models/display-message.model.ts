import { ChatMessageView } from './chat.models';

/** Discriminated union on `type` - lets MessageItem's template switch cleanly on message type instead
 * of guessing from ad hoc string content every time it renders. */
export type DisplayMessage =
  | { type: 'text'; message: ChatMessageView }
  | { type: 'error'; message: ChatMessageView }
  | { type: 'table'; message: ChatMessageView; headers: string[]; rows: string[][] }
  | { type: 'confirmation'; message: ChatMessageView; workItemId: number; summary: string }
  | { type: 'screenshot'; message: ChatMessageView };
