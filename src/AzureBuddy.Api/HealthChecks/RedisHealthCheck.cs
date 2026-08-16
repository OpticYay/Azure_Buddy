using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace AzureBuddy.Api.HealthChecks;

/// <summary>
/// Readiness check, only registered when Redis is configured (see Program.cs - a single-instance
/// deployment with no Redis has nothing here to check). Unlike DatabaseHealthCheck, an unreachable Redis
/// doesn't mean this replica can't serve requests at all - chat history, Data Protection keys, and LLM
/// settings pub/sub all have documented in-memory/per-instance fallback behavior - but it does mean this
/// replica has silently dropped out of the shared state every other replica depends on, which is worth
/// surfacing to an orchestrator/monitoring system rather than staying quiet about.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisHealthCheck(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _multiplexer.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy("Redis is reachable.");
        }
        catch (RedisConnectionException ex)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
