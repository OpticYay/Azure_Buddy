// Mirrors AzureBuddy.Core/Llm/LlmSettingsModels.cs.

export interface LlmSettingsView {
  /** False until an admin has ever saved a change - the view still shows real, in-effect values in
   * that case (whatever appsettings.json's Llm section says), just not yet backed by a database row. */
  isStoredInDatabase: boolean;
  primaryProvider: string;
  useFallback: boolean;
  geminiModel: string;
  geminiBaseUrl: string;
  geminiTimeoutSeconds: number;
  /** "••••••••1234"-style, or null if no key has ever been saved. Same convention as
   * AdoSettingsView.maskedPat - the real key never reaches the browser. */
  maskedGeminiApiKey: string | null;
  ollamaModel: string;
  ollamaBaseUrl: string;
  ollamaNumCtx: number;
  ollamaTimeoutSeconds: number;
  updatedAt: string | null;
}

export interface SaveLlmSettingsRequest {
  primaryProvider: string;
  useFallback: boolean;
  geminiModel: string;
  geminiBaseUrl: string;
  geminiTimeoutSeconds: number;
  /** Null (or omitted) means "leave the stored key alone" - unlike the ADO PAT screen, this field is
   * NOT required on every save. See SaveLlmSettingsRequest's C# comment for why that's deliberate. */
  geminiApiKey: string | null;
  ollamaModel: string;
  ollamaBaseUrl: string;
  ollamaNumCtx: number;
  ollamaTimeoutSeconds: number;
}
