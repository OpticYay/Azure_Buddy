using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzureBuddy.Core.WorkItemStates;

/// <summary>
/// Reads/writes the admin-managed "valid states per work item type" table. Unlike LlmSettings this
/// isn't a singleton row - it's an app-wide, admin-editable list keyed by WorkItemType, read by every
/// user's update-state requests (see UpdateItemFlow and AdoWorkItemToolset.UpdateWorkItemAsync) but
/// only ever written through the Admin-only endpoints (WorkItemStatesAdminController).
/// </summary>
public sealed class WorkItemStateConfigService
{
    private readonly AppDbContext _dbContext;

    public WorkItemStateConfigService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<WorkItemTypeStatesView>> GetAllGroupedAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.WorkItemStateConfigurations
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
    public Task<List<string>> GetEnabledStateNamesAsync(string workItemType, CancellationToken cancellationToken = default) =>
        _dbContext.WorkItemStateConfigurations
            .Where(s => s.WorkItemType == workItemType && s.IsEnabled)
            .OrderBy(s => s.DisplayOrder)
            .Select(s => s.StateName)
            .ToListAsync(cancellationToken);

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
        return true;
    }

    private static WorkItemStateView ToView(WorkItemStateConfiguration row) =>
        new(row.Id, row.WorkItemType, row.StateName, row.DisplayOrder, row.IsEnabled, row.UpdatedAt);
}
