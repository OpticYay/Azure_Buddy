import { classifyMessage } from './message-classifier';
import { ChatMessageView } from '../models/chat.models';

describe('classifyMessage', () => {
  const baseMessage: ChatMessageView = {
    id: '1',
    role: 'Assistant',
    content: 'hello',
    type: 'Text',
    workItemId: null,
    table: null,
    adoAttachmentUrl: null,
    createdAt: '2026-01-01T00:00:00Z',
  };

  it('maps type Confirmation to a confirmation display message', () => {
    const message: ChatMessageView = {
      ...baseMessage,
      type: 'Confirmation',
      workItemId: 42,
      content: 'Bug #42 created.',
    };

    const result = classifyMessage(message);

    expect(result).toEqual({
      type: 'confirmation',
      message,
      workItemId: 42,
      summary: 'Bug #42 created.',
    });
  });

  it('defaults workItemId to 0 when a Confirmation message has none', () => {
    const message: ChatMessageView = { ...baseMessage, type: 'Confirmation', workItemId: null };

    const result = classifyMessage(message);

    expect(result.type).toBe('confirmation');
    expect((result as { workItemId: number }).workItemId).toBe(0);
  });

  it('maps type Table to a table display message using the message table data', () => {
    const message: ChatMessageView = {
      ...baseMessage,
      type: 'Table',
      table: { headers: ['ID', 'Title'], rows: [['1', 'Login bug']] },
    };

    const result = classifyMessage(message);

    expect(result).toEqual({
      type: 'table',
      message,
      headers: ['ID', 'Title'],
      rows: [['1', 'Login bug']],
    });
  });

  it('defaults headers/rows to empty arrays when a Table message has no table data', () => {
    const message: ChatMessageView = { ...baseMessage, type: 'Table', table: null };

    const result = classifyMessage(message);

    expect(result).toEqual({ type: 'table', message, headers: [], rows: [] });
  });

  it('maps type Error to an error display message', () => {
    const message: ChatMessageView = {
      ...baseMessage,
      type: 'Error',
      content: 'Azure DevOps is not configured.',
    };

    expect(classifyMessage(message)).toEqual({ type: 'error', message });
  });

  it('maps type Text to a text display message', () => {
    const message: ChatMessageView = { ...baseMessage, type: 'Text' };

    expect(classifyMessage(message)).toEqual({ type: 'text', message });
  });

  it('falls back to a text display message for an unrecognized type', () => {
    const message = { ...baseMessage, type: 'SomethingUnexpected' } as unknown as ChatMessageView;

    expect(classifyMessage(message)).toEqual({ type: 'text', message });
  });

  it('an ADO attachment URL wins over the tagged type, even for a Text message', () => {
    const message: ChatMessageView = {
      ...baseMessage,
      type: 'Text',
      adoAttachmentUrl: 'https://dev.azure.com/org/proj/attachments/1',
    };

    expect(classifyMessage(message)).toEqual({ type: 'screenshot', message });
  });

  it('an ADO attachment URL wins even over Confirmation/Table/Error types', () => {
    const message: ChatMessageView = {
      ...baseMessage,
      type: 'Confirmation',
      adoAttachmentUrl: 'https://dev.azure.com/attachments/2',
    };

    expect(classifyMessage(message)).toEqual({ type: 'screenshot', message });
  });
});
