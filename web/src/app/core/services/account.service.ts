import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { ProfileView, UpdateProfileRequest, UpdateProfileResponse } from '../models/account.models';
import { AuthService } from './auth.service';

const BASE_URL = `${environment.apiUrl}/api/account`;

/** Thin wrapper around api/account/* - any logged-in user can call these for their own account, no
 * admin role required. changePassword automatically attaches the caller's own current refresh token
 * (from AuthService) so the backend can spare this session from the other-sessions revocation it does
 * on a successful change - see ChangePasswordRequest's docs in account.models.ts. */
@Injectable({ providedIn: 'root' })
export class AccountService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  getProfile(): Observable<ProfileView> {
    return this.http.get<ProfileView>(`${BASE_URL}/profile`);
  }

  updateProfile(request: UpdateProfileRequest): Observable<UpdateProfileResponse> {
    return this.http.put<UpdateProfileResponse>(`${BASE_URL}/profile`, request);
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/change-password`, {
      currentPassword,
      newPassword,
      refreshToken: this.auth.getRefreshToken(),
    });
  }
}
