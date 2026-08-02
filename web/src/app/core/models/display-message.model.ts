import { ChatMessageView } from './chat.models';

/**
 * A "discriminated union" (also called a "tagged union"): a set of object shapes that all share one
 * common field (here, `type`) whose value is a specific string literal, not just `string`. TypeScript
 * uses that field to narrow which shape you're dealing with - e.g. `if (msg.type === 'table')` proves
 * to the compiler that `msg.rows` and `msg.headers` exist on that branch, the same way a C# switch
 * over an enum with pattern-matched records would. This is what lets MessageItem's template switch
 * cleanly on message type instead of guessing from ad hoc string content every time it renders.
 */
export type DisplayMessage =
  | { type: 'text'; message: ChatMessageView }
  | { type: 'error'; message: ChatMessageView }
  | { type: 'table'; message: ChatMessageView; headers: string[]; rows: string[][] }
  | { type: 'confirmation'; message: ChatMessageView; workItemId: number; summary: string }
  | { type: 'screenshot'; message: ChatMessageView };
