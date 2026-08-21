import { ChatMessageView } from '../models/chat.models';
import { DisplayMessage } from '../models/display-message.model';

// This used to be a pile of regexes/markdown-table-parsing that reverse-engineered a message's "type"
// from its plain-text content, because the backend only ever returned an untagged string (see this
// file's git history for what that looked like). The backend now tags every message with a real
// `type` field (and structured `table`/`workItemId` data) at the point it's created - see
// AzureBuddy.Data.Entities.ChatMessageType and FlowResult.cs - so this function is now a direct,
// reliable mapping instead of a guess. The one thing that's STILL a real limitation, not fixed by
// that change: the free-form conversational agent (AzureBuddyAgent) writes its own prose replies and
// always tags them 'Text', even if the reply happens to read like a list or confirmation - only the
// deterministic flows (create/view/update/my-items) produce the richer types.
export function classifyMessage(message: ChatMessageView): DisplayMessage {
  // A real, backend-provided field - not a guess - so this check comes first and wins regardless of
  // what type the message was otherwise tagged with (a screenshot's caption is still plain text, but
  // the presence of a real ADO attachment URL is what makes it worth rendering as a screenshot card).
  if (message.adoAttachmentUrl) {
    return { type: 'screenshot', message };
  }

  switch (message.type) {
    case 'Confirmation':
      return {
        type: 'confirmation',
        message,
        workItemId: message.workItemId ?? 0,
        summary: message.content,
      };
    case 'Table':
      return {
        type: 'table',
        message,
        headers: message.table?.headers ?? [],
        rows: message.table?.rows ?? [],
      };
    case 'Error':
      return { type: 'error', message };
    case 'Text':
    default:
      return { type: 'text', message };
  }
}
