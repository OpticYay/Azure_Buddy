import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  CreateWorkItemStateRequest,
  UpdateWorkItemStateRequest,
  WorkItemStateView,
  WorkItemTypeStatesView,
} from '../models/work-item-states.models';

const BASE_URL = `${environment.apiUrl}/api/admin/work-item-states`;

/** Thin wrapper around the api/admin/work-item-states CRUD endpoints - same shape as
 * LlmSettingsService. Only ever called from the admin work-item-states screen; the backend's own
 * [Authorize(Roles = "Admin")] is the real gate, this service doesn't add any client-side check. */
@Injectable({ providedIn: 'root' })
export class WorkItemStatesService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<WorkItemTypeStatesView[]> {
    return this.http.get<WorkItemTypeStatesView[]>(BASE_URL);
  }

  create(request: CreateWorkItemStateRequest): Observable<WorkItemStateView> {
    return this.http.post<WorkItemStateView>(BASE_URL, request);
  }

  update(id: number, request: UpdateWorkItemStateRequest): Observable<WorkItemStateView> {
    return this.http.put<WorkItemStateView>(`${BASE_URL}/${id}`, request);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${BASE_URL}/${id}`);
  }
}
