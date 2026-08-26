using System.Text.Json;
using AzureBuddy.Api;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Llm;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests;

/// <summary>Unit tests for GlobalExceptionHandler.Classify - enumerates every known exception type and
/// asserts its documented status code/error code, using a bare DefaultHttpContext rather than a full
/// WebApplicationFactory since this only needs an HttpContext, not the whole pipeline.</summary>
public class GlobalExceptionHandlerTests
{
    private sealed record ParsedError(string Code, string Message, string? Field = null);
    private sealed record ParsedErrorResponse(IReadOnlyList<ParsedError> Errors);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(int StatusCode, ParsedErrorResponse Body)> HandleAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        Assert.True(handled);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = JsonSerializer.Deserialize<ParsedErrorResponse>(await reader.ReadToEndAsync(), JsonOptions);

        return (context.Response.StatusCode, body!);
    }

    [Fact]
    public async Task AdoNotConfiguredException_MapsTo400WithAdoNotConfiguredCode()
    {
        var (statusCode, body) = await HandleAsync(new AdoNotConfiguredException("Connect your Azure DevOps account first."));

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal("ado_not_configured", body.Errors[0].Code);
        Assert.Equal("Connect your Azure DevOps account first.", body.Errors[0].Message);
    }

    [Fact]
    public async Task AdoApiException_MapsTo502WithAdoApiErrorCode()
    {
        var (statusCode, body) = await HandleAsync(new AdoApiException("Azure DevOps returned 503."));

        Assert.Equal(StatusCodes.Status502BadGateway, statusCode);
        Assert.Equal("ado_api_error", body.Errors[0].Code);
        Assert.Equal("Azure DevOps returned 503.", body.Errors[0].Message);
    }

    [Fact]
    public async Task ChatCompletionProviderException_MapsTo502WithGenericSafeMessage_NotTheRawExceptionMessage()
    {
        var (statusCode, body) = await HandleAsync(new ChatCompletionProviderException("Gemini", "internal detail that should not leak"));

        Assert.Equal(StatusCodes.Status502BadGateway, statusCode);
        Assert.Equal("llm_provider_unavailable", body.Errors[0].Code);
        Assert.DoesNotContain("internal detail", body.Errors[0].Message);
    }

    [Fact]
    public async Task UnrecognizedException_MapsTo500WithGenericMessage_NeverLeakingExceptionInternals()
    {
        var (statusCode, body) = await HandleAsync(new InvalidOperationException("connection string: Server=prod-db;Password=hunter2"));

        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);
        Assert.Equal("unexpected_error", body.Errors[0].Code);
        Assert.DoesNotContain("hunter2", body.Errors[0].Message);
        Assert.DoesNotContain("connection string", body.Errors[0].Message);
    }
}
