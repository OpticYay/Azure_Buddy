import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { LlmSettingsView, SaveLlmSettingsRequest } from '../models/llm-settings.models';

const BASE_URL = `${environment.apiUrl}/api/admin/llm`;

/** Thin wrapper around the two api/admin/llm endpoints - same shape as AdoSettingsService. Only ever
 * called from the admin LLM settings screen; the backend's own [Authorize(Roles = "Admin")] is the
 * real gate, this service doesn't add any client-side check of its own. */
@Injectable({ providedIn: 'root' })
export class LlmSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<LlmSettingsView> {
    return this.http.get<LlmSettingsView>(BASE_URL);
  }

  save(request: SaveLlmSettingsRequest): Observable<LlmSettingsView> {
    return this.http.put<LlmSettingsView>(BASE_URL, request);
  }
}
