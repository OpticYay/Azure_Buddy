using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Llm;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AzureBuddy.Api;

/// <summary>
/// Catches anything that escaped every controller/service-level try/catch and turns it into a
/// consistent JSON error body (RFC 7807 ProblemDetails) instead of the framework default: a raw stack
/// trace page in Development, or a bare empty 500 in Production. Registered via
/// builder.Services.AddExceptionHandler&lt;GlobalExceptionHandler&gt;() + app.UseExceptionHandler() in
/// Program.cs - this is the .NET 8 IExceptionHandler pattern, not app.UseExceptionHandler(lambda).
///
/// Known exception types get a specific status code and a message that's safe to show the client.
/// Anything unrecognized gets a generic 500 message - full exception details are logged server-side
/// (never put exception internals in the response body for unrecognized exceptions; they might
/// contain connection strings, stack frames referencing internal file paths, etc.).
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
        var (statusCode, title, detail) = Classify(exception);

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "{Title} on {Method} {Path}.", title, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        }, cancellationToken);

        return true;
    }

    private static (int StatusCode, string Title, string Detail) Classify(Exception exception) => exception switch
    {
        AdoNotConfiguredException ex => (StatusCodes.Status400BadRequest, "Azure DevOps not configured", ex.Message),
        AdoApiException ex => (StatusCodes.Status502BadGateway, "Azure DevOps request failed", ex.Message),
        ChatCompletionProviderException => (StatusCodes.Status502BadGateway, "AI provider unavailable", "All configured AI providers failed to respond. Please try again shortly."),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "An unexpected error occurred. Please try again or contact support if this persists.")
    };
}
