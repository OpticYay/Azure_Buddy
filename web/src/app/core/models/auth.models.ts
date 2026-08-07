// These "interface" declarations aren't Angular-specific - they're plain TypeScript. An interface
// describes the *shape* of an object (what properties it has and their types) without providing any
// implementation. Unlike a C# class, nothing here exists at runtime - TypeScript interfaces are erased
// completely when compiled to JavaScript. They exist purely so the editor/compiler can catch mistakes
// like `user.emial` (typo) or passing a string where a number is expected, before you ever run the code.
//
// Each interface below matches one of the backend's C# records exactly, field-for-field, so there's
// never a guessing game about what shape a response has - see AzureBuddy.Core/Auth/AuthModels.cs.
// ASP.NET Core's default JSON serialization lowercases the first letter of each property
// (OrganizationUrl -> organizationUrl), which is why these fields are camelCase even though the C#
// side is PascalCase.

export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

// Mirrors AuthTokens on the backend. accessTokenExpiresAtUtc arrives as an ISO date *string* over
// JSON (JSON has no native "date" type) - if you need to do date math with it, wrap it in `new Date(...)`.
export interface AuthTokens {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
}

// What the backend sends back on a failed register/login/refresh (400/401) is now the shared
// ApiErrorResponse shape (see api-error.model.ts) - previously its own one-off {errors: string[]}.

export interface ForgotPasswordRequest {
  email: string;
}

export interface ResetPasswordRequest {
  email: string;
  token: string;
  newPassword: string;
}

export interface ConfirmEmailRequest {
  email: string;
  token: string;
}

export interface ResendConfirmationRequest {
  email: string;
}
