using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace AzureBuddy.Core.AzureDevOps;

public static class AdoServiceCollectionExtensions
{
    public static IServiceCollection AddAzureDevOps(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AdoOptions>(configuration.GetSection(AdoOptions.SectionName));

        // No BaseAddress and no auth DelegatingHandler anymore - both varied per user, so AdoClient
        // now builds the full URL and Basic Auth header itself from the AdoConnectionContext passed
        // into each call (see AdoClient.BuildUrl/SendAsync).
        services.AddHttpClient<IAdoClient, AdoClient>()
            .AddPolicyHandler(GetRetryPolicy());

        // Scoped = one instance per HTTP request, which is exactly the lifetime "current user's ADO
        // connection for this request" needs.
        services.AddScoped<AdoConnectionContextAccessor>();

        services.AddScoped<IAdoAttachmentService, AdoAttachmentService>();

        return services;
    }

    /// <summary>2 tries total, 1s wait between - mirrors every httpRequest(Tool) node's
    /// retryOnFail:true, maxTries:2, waitBetweenTries:1000 in the original n8n workflow.</summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(retryCount: 1, _ => TimeSpan.FromSeconds(1));
}
