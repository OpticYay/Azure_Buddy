namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Replaces the old global AdoOptions.Organization/Project/PersonalAccessToken: every ADO call is now
/// scoped to one specific user's connection details, resolved from their stored UserAdoSettings row
/// and passed explicitly into every IAdoClient call. Pat here is always the DECRYPTED value - it only
/// exists in memory for the lifetime of a single request, never written back to storage or logged.
/// </summary>
public sealed record AdoConnectionContext(string OrganizationUrl, string Project, string Pat);
