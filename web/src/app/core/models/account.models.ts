// Mirrors AzureBuddy.Core/Account/AccountModels.cs and AccountController's response wrapper.

export interface ProfileView {
  email: string;
  displayName: string;
  roles: string[];
}

export interface UpdateProfileRequest {
  displayName: string;
  email: string;
}

/** requiresReLogin is true only when email actually changed - the JWT bakes email in as a claim at
 * mint time (see TokenService.CreateAccessToken), so an email change needs a fresh token to stop
 * showing the old address everywhere. A display-name-only save is always false here. */
export interface UpdateProfileResponse {
  profile: ProfileView;
  requiresReLogin: boolean;
}

/** refreshToken is optional and, when supplied, is the ONLY session change-password will not revoke -
 * see AccountService.ChangePasswordAsync's docs on the backend. AccountService (frontend) fills this
 * in from AuthService's stored refresh token automatically; callers don't need to pass it themselves. */
export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
  refreshToken: string | null;
}
