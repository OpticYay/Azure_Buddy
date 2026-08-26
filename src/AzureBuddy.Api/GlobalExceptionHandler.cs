using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Llm;
using Microsoft.AspNetCore.Diagnostics;

namespace AzureBuddy.Api;

/// <summary>
/// Catches anything that escaped every controller/service-level try/catch and turns it into the same
/// {errors: [{code, message, field?}]} body every other error response in this API uses (see
/// AzureBuddy.Core.Common.ApiErrorResponse) instead of the framework default: a raw stack trace page in
/// Development, or a bare empty 500 in Production. Previously this used RFC 7807 ProblemDetails
/// ({title, detail, status, instance}) - a perfectly standard shape on its own, but a DIFFERENT shape
/// than AuthController's {errors: string[]} and the screenshot endpoint's bare-string error body,
/// which meant the frontend needed three separate parsing functions just to show an error message.
/// Registered via builder.Services.AddExceptionHandler&lt;GlobalExceptionHandler&gt;() +
/// app.UseExceptionHandler() in Program.cs - this is the .NET 8 IExceptionHandler pattern, not
/// app.UseExceptionHandler(lambda).
///
/// Known exception types get a specific status code, code, and a message that's safe to show the
/// client. Anything unrecognized gets a generic 500 message - full exception details are logged
/// server-side (never put exception internals in the response body for unrecognized exceptions; they
/// might contain connection strings, stack frames referencing internal file paths, etc.).
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, error) = Classify(exception);

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "{Code} on {Method} {Path}.", error.Code, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new ApiErrorResponse(error), cancellationToken);

        return true;
    }

    private static (int StatusCode, ApiError Error) Classify(Exception exception) => exception switch
    {
        AdoNotConfiguredException ex => (StatusCodes.Status400BadRequest, new ApiError("ado_not_configured", ex.Message)),
        AdoApiException ex => (StatusCodes.Status502BadGateway, new ApiError("ado_api_error", ex.Message)),
        ChatCompletionProviderException => (StatusCodes.Status502BadGateway, new ApiError("llm_provider_unavailable", "All configured AI providers failed to respond. Please try again shortly.")),
        LlmNotConfiguredException ex => (StatusCodes.Status503ServiceUnavailable, new ApiError("llm_not_configured", ex.Message)),
        _ => (StatusCodes.Status500InternalServerError, new ApiError("unexpected_error", "An unexpected error occurred. Please try again or contact support if this persists."))
    };
}
