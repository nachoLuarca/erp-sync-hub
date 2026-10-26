using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Inventory;

/// <summary>Registro de las ejecuciones de la sincronización de stock y precios en la tabla <c>inventory_sync_runs</c>.</summary>
internal sealed class InventorySyncRunStore(ErpSyncHubDbContext context, TimeProvider time) : IInventorySyncRunStore
{
    public async Task AddAsync(InventorySyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        context.InventorySyncRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(InventorySyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (context.Entry(run).State == EntityState.Detached)
        {
            context.InventorySyncRuns.Update(run);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<InventorySyncRun?> GetLastAsync(CancellationToken cancellationToken) =>
        context.InventorySyncRuns
            .AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<InventorySyncRun>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return await context.InventorySyncRuns
            .AsNoTracking()
            .OrderByDescending(run => run.StartedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task<InventorySyncRun?> GetLastSucceededAsync(CancellationToken cancellationToken) =>
        context.InventorySyncRuns
            .AsNoTracking()
            .Where(run => run.Status == InventorySyncStatus.Succeeded)
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<DateTimeOffset?> GetLastSucceededStartAsync(CancellationToken cancellationToken) =>
        await context.InventorySyncRuns
            .AsNoTracking()
            .Where(run => run.Status == InventorySyncStatus.Succeeded)
            .OrderByDescending(run => run.StartedAt)
            .Select(run => (DateTimeOffset?)run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> AbandonRunningAsync(CancellationToken cancellationToken)
    {
        var stale = await context.InventorySyncRuns
            .Where(run => run.Status == InventorySyncStatus.Running)
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        foreach (var run in stale)
        {
            run.Abandon(now);
        }

        if (stale.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }
}
