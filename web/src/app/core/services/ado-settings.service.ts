import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  AdoSettingsView,
  SaveAdoSettingsRequest,
  TestConnectionResult,
} from '../models/settings.models';

const BASE_URL = `${environment.apiUrl}/api/settings/ado`;

/** Thin wrapper around the four /api/settings/ado endpoints - no logic beyond making the HTTP calls,
 * since (unlike AuthService) there's no cross-component state to coordinate here: whichever component
 * needs this data just asks for it directly. */
@Injectable({ providedIn: 'root' })
export class AdoSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<AdoSettingsView> {
    return this.http.get<AdoSettingsView>(BASE_URL);
  }

  save(request: SaveAdoSettingsRequest): Observable<AdoSettingsView> {
    return this.http.put<AdoSettingsView>(BASE_URL, request);
  }

  delete(): Observable<void> {
    return this.http.delete<void>(BASE_URL);
  }

  testConnection(): Observable<TestConnectionResult> {
    return this.http.post<TestConnectionResult>(`${BASE_URL}/test-connection`, {});
  }
}
