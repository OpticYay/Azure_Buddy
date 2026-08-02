namespace AzureBuddy.Core.Common;

/// <summary>
/// One error, in the single shape every error response across this API now uses. `Code` is a stable,
/// machine-readable identifier a frontend can switch on (e.g. to pick an icon or a specific retry
/// action) without parsing human text; `Message` is what a user actually reads; `Field` is set only
/// when the error applies to one specific input (e.g. a form validation failure) so the frontend can
/// show it next to that field instead of in a generic banner.
/// </summary>
public sealed record ApiError(string Code, string Message, string? Field = null);

/// <summary>The one response body shape for every non-2xx response in this API (except the
/// screenshot-attach endpoint's embedded failure case, which has its own reasons - see
/// AppendMessageResult). Always a list, never a single error, since some failures (e.g. registration)
/// can have more than one problem at once.</summary>
public sealed record ApiErrorResponse(IReadOnlyList<ApiError> Errors)
{
    public ApiErrorResponse(ApiError error) : this(new[] { error })
    {
    }
}
