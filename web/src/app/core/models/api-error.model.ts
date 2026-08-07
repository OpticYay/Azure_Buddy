// Mirrors AzureBuddy.Core.Common.ApiError / ApiErrorResponse - the ONE error shape every non-2xx
// response in the API now uses (auth failures, ADO settings validation, chat errors, everything).
// Previously each endpoint had its own shape (a bare string array, RFC 7807 ProblemDetails, or a raw
// string) and the frontend needed a different parsing function for each one - see the git history of
// ado-settings.ts's extractErrorMessage and message-composer.ts's error handling for what that looked
// like before this was unified.
export interface ApiError {
  /** Stable, machine-readable identifier (e.g. "invalid_credentials", "ado_not_configured") - safe to
   * switch on in code, unlike Message which is meant for display and could change wording. */
  code: string;
  /** Human-readable text, safe to show directly to the user. */
  message: string;
  /** Set only when this error applies to one specific form field (e.g. "password", "organizationUrl") -
   * lets a form show the error next to the right input instead of in a generic banner. */
  field: string | null;
}

export interface ApiErrorResponse {
  errors: ApiError[];
}

/** Every service's error handling converges on this: given an HttpErrorResponse whose body is (or
 * should be) an ApiErrorResponse, pull out a single displayable message. Falls back gracefully for the
 * rare non-ApiErrorResponse case (e.g. a network failure with no body at all). */
export function extractApiErrorMessage(errorBody: unknown, fallback: string): string {
  const body = errorBody as Partial<ApiErrorResponse> | undefined;
  const messages = body?.errors?.map((e) => e.message).filter(Boolean);
  return messages && messages.length > 0 ? messages.join(' ') : fallback;
}

/** Companion to extractApiErrorMessage for forms that want a field-specific error shown next to the
 * relevant input (e.g. change-password's "wrong current password" belonging on that one field) instead
 * of - or in addition to - a generic banner. Errors with no `field` set are simply absent from the
 * returned map; callers typically still call extractApiErrorMessage too for a fallback banner covering
 * those. */
export function extractFieldErrors(errorBody: unknown): Record<string, string> {
  const body = errorBody as Partial<ApiErrorResponse> | undefined;
  const result: Record<string, string> = {};
  for (const error of body?.errors ?? []) {
    if (error.field) {
      result[error.field] = error.message;
    }
  }
  return result;
}
