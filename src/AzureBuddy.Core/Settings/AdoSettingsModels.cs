namespace AzureBuddy.Core.Settings;

public sealed record SaveAdoSettingsRequest(string OrganizationUrl, string DefaultProject, string PersonalAccessToken);

/// <summary>What GET /api/settings/ado returns - notice there is no field carrying the real PAT,
/// only MaskedPat (e.g. "••••••••1234"). This is the type returned to the client; it's constructed
/// from the stored entity but the plaintext/encrypted PAT never becomes a property on it.</summary>
public sealed record AdoSettingsView(bool IsConfigured, string? OrganizationUrl, string? DefaultProject, string? MaskedPat, DateTime? UpdatedAt);

public sealed record TestConnectionResult(bool Success, string? Error);
