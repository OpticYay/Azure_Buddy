using AzureBuddy.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBuddy.Api.HealthChecks;

/// <summary>Readiness check, not liveness: MySQL is the source of truth for everything this app does,
/// so a replica that can't reach it can't correctly serve a single request and shouldn't receive
/// traffic - but a transient DB blip also shouldn't make an orchestrator kill and restart the process
/// (which is what a failing liveness check does), hence this is tagged "ready" only.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;

    public DatabaseHealthCheck(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("MySQL is reachable.")
            : HealthCheckResult.Unhealthy("MySQL is not reachable.");
    }
}
