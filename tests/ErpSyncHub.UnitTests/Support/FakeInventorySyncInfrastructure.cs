using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.UnitTests.Support;

/// <summary>Registro de ejecuciones en memoria, con la misma regla de corte que el real: solo cuentan las exitosas.</summary>
internal sealed class FakeInventorySyncRunStore : IInventorySyncRunStore
{
    public List<InventorySyncRun> Runs { get; } = [];

    /// <summary>Cuántas veces se guardó el cierre de una ejecución.</summary>
    public int Saves { get; private set; }

    public Task AddAsync(InventorySyncRun run, CancellationToken cancellationToken)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task SaveAsync(InventorySyncRun run, CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }

    public Task<InventorySyncRun?> GetLastAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Runs.OrderByDescending(run => run.StartedAt).FirstOrDefault());

    public Task<IReadOnlyList<InventorySyncRun>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventorySyncRun>>([.. Runs.OrderByDescending(run => run.StartedAt).Take(limit)]);

    public Task<DateTimeOffset?> GetLastSucceededStartAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Runs
            .Where(run => run.Status == InventorySyncStatus.Succeeded)
            .OrderByDescending(run => run.StartedAt)
            .Select(run => (DateTimeOffset?)run.StartedAt)
            .FirstOrDefault());

    public Task<int> AbandonRunningAsync(CancellationToken cancellationToken)
    {
        var running = Runs.Where(run => run.Status == InventorySyncStatus.Running).ToList();
        foreach (var run in running)
        {
            run.Abandon(DateTimeOffset.UnixEpoch);
        }

        return Task.FromResult(running.Count);
    }
}

/// <summary>Una exclusión mutua que se puede ocupar a voluntad y que cuenta cuántas veces se tomó y se soltó.</summary>
internal sealed class FakeInventorySyncLock : IInventorySyncLock
{
    /// <summary>Si es verdadero, otra ejecución tiene el lock y <see cref="TryAcquireAsync"/> devuelve <see langword="null"/>.</summary>
    public bool HeldByOther { get; set; }

    public int Acquired { get; private set; }

    public int Released { get; private set; }

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (HeldByOther)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        Acquired++;
        return Task.FromResult<IAsyncDisposable?>(new Handle(this));
    }

    private sealed class Handle(FakeInventorySyncLock owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Released++;
            return ValueTask.CompletedTask;
        }
    }
}
