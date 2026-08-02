// Mirrors AzureBuddy.Core/Settings/AdoSettingsModels.cs.

export interface AdoSettingsView {
  isConfigured: boolean;
  organizationUrl: string | null;
  defaultProject: string | null;
  /** e.g. "••••••••1234" - the backend NEVER sends the real PAT back, only this masked display value. */
  maskedPat: string | null;
  updatedAt: string | null;
}

export interface SaveAdoSettingsRequest {
  organizationUrl: string;
  defaultProject: string;
  personalAccessToken: string;
}

export interface TestConnectionResult {
  success: boolean;
  /** Populated when success is false - the actual reason from Azure DevOps, not a generic message. */
  error: string | null;
}
