import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';

import { ChatService } from './chat.service';
import { environment } from '../../../environments/environment';
import { ChatSessionDetail, ChatResponse, AppendMessageResult } from '../models/chat.models';

describe('ChatService', () => {
  let service: ChatService;
  let httpMock: HttpTestingController;

  const CHATS_BASE_URL = `${environment.apiUrl}/api/chats`;
  const LIVE_CHAT_URL = `${environment.apiUrl}/api/chat`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ChatService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listSessions GETs the chats endpoint with page/pageSize query params', () => {
    service.listSessions(2, 10).subscribe();

    const req = httpMock.expectOne((r) => r.url === CHATS_BASE_URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('10');
    req.flush({ items: [], page: 2, pageSize: 10, totalCount: 0, hasMore: false });
  });

  it('getSession GETs a single session by id', () => {
    service.getSession('session-1').subscribe();

    const req = httpMock.expectOne(`${CHATS_BASE_URL}/session-1`);
    expect(req.request.method).toBe('GET');
    req.flush({
      id: 'session-1',
      title: 't',
      createdAt: '',
      updatedAt: '',
      messages: [],
    } satisfies ChatSessionDetail);
  });

  it('createSession POSTs the title as the request body', () => {
    service.createSession('My session').subscribe();

    const req = httpMock.expectOne(CHATS_BASE_URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ title: 'My session' });
    req.flush({
      id: '1',
      title: 'My session',
      createdAt: '',
      updatedAt: '',
      messages: [],
    } satisfies ChatSessionDetail);
  });

  it('deleteSession DELETEs the session by id', () => {
    service.deleteSession('session-1').subscribe();

    const req = httpMock.expectOne(`${CHATS_BASE_URL}/session-1`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('renameSession PUTs the new title to the session title endpoint', () => {
    service.renameSession('session-1', 'New title').subscribe();

    const req = httpMock.expectOne(`${CHATS_BASE_URL}/session-1/title`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ title: 'New title' });
    req.flush({ id: 'session-1', title: 'New title', createdAt: '', updatedAt: '' });
  });

  it('sendMessage POSTs sessionId and message to the live /chat endpoint', () => {
    service.sendMessage('session-1', 'hello').subscribe();

    const req = httpMock.expectOne(LIVE_CHAT_URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ sessionId: 'session-1', message: 'hello' });
    req.flush({
      sessionId: 'session-1',
      reply: 'hi',
      type: 'Text',
      workItemId: null,
      table: null,
    } satisfies ChatResponse);
  });

  it('sendMessage passes a null sessionId for a brand-new conversation', () => {
    service.sendMessage(null, 'hello').subscribe();

    const req = httpMock.expectOne(LIVE_CHAT_URL);
    expect(req.request.body).toEqual({ sessionId: null, message: 'hello' });
    req.flush({
      sessionId: 'new-session',
      reply: 'hi',
      type: 'Text',
      workItemId: null,
      table: null,
    } satisfies ChatResponse);
  });

  it('appendScreenshotMessage POSTs a multipart form with role/content/workItemId/screenshot', () => {
    const file = new File(['fake image bytes'], 'bug.png', { type: 'image/png' });

    service.appendScreenshotMessage('session-1', 'here is what I see', 42, file).subscribe();

    const req = httpMock.expectOne(`${CHATS_BASE_URL}/session-1/messages`);
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect(body instanceof FormData).toBe(true);
    expect(body.get('role')).toBe('User');
    expect(body.get('content')).toBe('here is what I see');
    expect(body.get('workItemId')).toBe('42');
    expect((body.get('screenshot') as File).name).toBe('bug.png');

    req.flush({
      success: true,
      message: {
        id: '1',
        role: 'User',
        content: 'here is what I see',
        adoAttachmentUrl: 'https://dev.azure.com/attachments/1',
        workItemId: 42,
        type: 'Text',
        table: null,
        createdAt: '',
      },
      error: null,
    } satisfies AppendMessageResult);
  });
});
