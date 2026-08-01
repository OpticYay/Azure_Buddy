using System.ComponentModel.DataAnnotations;

namespace AzureBuddy.Core.Settings;

// [Url] catches an obviously malformed OrganizationUrl (e.g. missing scheme) before it ever reaches
// an ADO call - previously this was only checked for non-empty in SettingsController, so a typo'd
// URL wasn't caught until the first failed request. Attributes target the parameter (not
// "[property: ...]") - see the comment on RegisterRequest in Auth/AuthModels.cs for why that
// placement matters for records.
public sealed record SaveAdoSettingsRequest(
    [Required, Url] string OrganizationUrl,
    [Required] string DefaultProject,
    [Required] string PersonalAccessToken);

/// <summary>What GET /api/settings/ado returns - notice there is no field carrying the real PAT,
/// only MaskedPat (e.g. "••••••••1234"). This is the type returned to the client; it's constructed
/// from the stored entity but the plaintext/encrypted PAT never becomes a property on it.</summary>
public sealed record AdoSettingsView(bool IsConfigured, string? OrganizationUrl, string? DefaultProject, string? MaskedPat, DateTime? UpdatedAt);

public sealed record TestConnectionResult(bool Success, string? Error);
