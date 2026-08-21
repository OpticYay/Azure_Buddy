using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AzureBuddy.Core.WorkItemStates;

/// <summary>
/// Reads/writes the admin-managed "valid states per work item type" table. Unlike LlmSettings this
/// isn't a singleton row - it's an app-wide, admin-editable list keyed by WorkItemType, read by every
/// user's update-state requests (see UpdateItemFlow and AdoWorkItemToolset.UpdateWorkItemAsync) but
/// only ever written through the Admin-only endpoints (WorkItemStatesAdminController).
/// </summary>
public sealed class WorkItemStateConfigService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;

    public WorkItemStateConfigService(AppDbContext dbContext, IMemoryCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    private static string EnabledStateNamesCacheKey(string workItemType) => $"WorkItemStateConfig:EnabledStateNames:{workItemType}";

    public async Task<IReadOnlyList<WorkItemTypeStatesView>> GetAllGroupedAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.WorkItemStateConfigurations
            .AsNoTracking()
            .OrderBy(s => s.WorkItemType)
            .ThenBy(s => s.DisplayOrder)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(s => s.WorkItemType)
            .Select(g => new WorkItemTypeStatesView(g.Key, g.Select(ToView).ToList()))
            .ToList();
    }

    /// <summary>Enabled state names for one work item type, in display order. An empty list means "no
    /// admin config exists for this type yet" - callers (UpdateItemFlow, AdoWorkItemToolset) treat
    /// that as "nothing to validate against" and fall back to letting Azure DevOps itself accept or
    /// reject the state, rather than blocking every update for a type nobody has configured.</summary>
    public async Task<List<string>> GetEnabledStateNamesAsync(string workItemType, CancellationToken cancellationToken = default)
    {
        var cacheKey = EnabledStateNamesCacheKey(workItemType);
        if (_cache.TryGetValue(cacheKey, out List<string>? cached))
        {
            return cached!;
        }

        var names = await _dbContext.WorkItemStateConfigurations
            .AsNoTracking()
            .Where(s => s.WorkItemType == workItemType && s.IsEnabled)
            .OrderBy(s => s.DisplayOrder)
            .Select(s => s.StateName)
            .ToListAsync(cancellationToken);

        // Short TTL rather than event-driven invalidation across replicas: the write paths below
        // (Create/Update/Delete) already evict this process's own cache entry immediately, so the TTL
        // only matters for other replicas picking up an admin's change - acceptable staleness for a
        // config that changes rarely.
        _cache.Set(cacheKey, names, CacheDuration);
        return names;
    }

    public async Task<WorkItemStateView> CreateAsync(CreateWorkItemStateRequest request, CancellationToken cancellationToken = default)
    {
        var row = new WorkItemStateConfiguration
        {
            WorkItemType = request.WorkItemType.Trim(),
            StateName = request.StateName.Trim(),
            DisplayOrder = request.DisplayOrder,
            IsEnabled = request.IsEnabled,
        };

        _dbContext.WorkItemStateConfigurations.Add(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(EnabledStateNamesCacheKey(row.WorkItemType));
        return ToView(row);
    }

    public async Task<WorkItemStateView?> UpdateAsync(int id, UpdateWorkItemStateRequest request, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.WorkItemStateConfigurations.FindAsync(new object[] { id }, cancellationToken);
        if (row is null)
        {
            return null;
        }

        row.StateName = request.StateName.Trim();
        row.DisplayOrder = request.DisplayOrder;
        row.IsEnabled = request.IsEnabled;
        row.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(EnabledStateNamesCacheKey(row.WorkItemType));
        return ToView(row);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.WorkItemStateConfigurations.FindAsync(new object[] { id }, cancellationToken);
        if (row is null)
        {
            return false;
        }

        _dbContext.WorkItemStateConfigurations.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(EnabledStateNamesCacheKey(row.WorkItemType));
        return true;
    }

    private static WorkItemStateView ToView(WorkItemStateConfiguration row) =>
        new(row.Id, row.WorkItemType, row.StateName, row.DisplayOrder, row.IsEnabled, row.UpdatedAt);
}
