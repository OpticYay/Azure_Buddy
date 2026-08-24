namespace AzureBuddy.Core.AzureDevOps;

/// <summary>Binds to the "Ado" config section. Only the API version lives here now - it's an
/// app-wide, non-secret constant, unlike organization/project/PAT which moved to per-user
/// UserAdoSettings (see AdoConnectionContext).</summary>
public sealed class AdoOptions
{
    public const string SectionName = "Ado";

    public string ApiVersion { get; init; } = "7.1";

    /// <summary>Cap on ids returned from an LLM-authored WIQL query (see WiqlFragmentCompiler) - matches
    /// GetWorkItemsAsync's single-URL `?ids=` batch call, which does not chunk across multiple requests.</summary>
    public int MaxWiqlResults { get; init; } = 200;

    /// <summary>Host for the Identities REST API, which is account-wide rather than project-scoped and
    /// therefore lives on a different host than every other AdoClient call - see AdoIdentityResolver.</summary>
    public string IdentityBaseUrl { get; init; } = "https://vssps.dev.azure.com";

    /// <summary>How long an uploaded-but-not-yet-attached file waits in IPendingAttachmentStore before
    /// it's evicted.</summary>
    public int PendingAttachmentTtlMinutes { get; init; } = 15;
}
