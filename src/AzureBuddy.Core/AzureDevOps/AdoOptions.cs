namespace AzureBuddy.Core.AzureDevOps;

/// <summary>Binds to the "Ado" config section. Only the API version lives here now - it's an
/// app-wide, non-secret constant, unlike organization/project/PAT which moved to per-user
/// UserAdoSettings (see AdoConnectionContext).</summary>
public sealed class AdoOptions
{
    public const string SectionName = "Ado";

    public string ApiVersion { get; init; } = "7.1";
}
