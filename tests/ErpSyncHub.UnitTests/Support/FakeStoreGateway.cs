using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.UnitTests.Support;

/// <summary>Una tienda en memoria: productos por SKU, registro de lo recibido y la posibilidad de hacer fallar cada operación.</summary>
internal sealed class FakeStoreGateway : IStoreGateway
{
    private long _nextId = 100;

    public Dictionary<string, long> Products { get; } = new(StringComparer.Ordinal);

    /// <summary>Cada llamada a <see cref="UpdateProductsAsync"/>, con lo que recibió.</summary>
    public List<IReadOnlyList<StoreProductUpdate>> UpdateCalls { get; } = [];

    public IEnumerable<StoreProductUpdate> Updates => UpdateCalls.SelectMany(call => call);

    /// <summary>Errores por SKU: ese producto falla y los demás se actualizan.</summary>
    public Dictionary<string, ResultError> ItemErrors { get; } = new(StringComparer.Ordinal);

    public ResultError? FindFailure { get; set; }

    public ResultError? UpdateFailure { get; set; }

    public int FindCalls { get; private set; }

    public void AddProduct(string sku) => Products[sku] = _nextId++;

    public Task<Result<IReadOnlyDictionary<string, StoreProduct>>> FindProductsBySkuAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        if (FindFailure is { } failure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyDictionary<string, StoreProduct>>(failure));
        }

        IReadOnlyDictionary<string, StoreProduct> found = skus
            .Where(Products.ContainsKey)
            .ToDictionary(sku => sku, sku => new StoreProduct(Products[sku], sku), StringComparer.Ordinal);
        return Task.FromResult(Result.Success(found));
    }

    public Task<Result<IReadOnlyList<StoreUpdateOutcome>>> UpdateProductsAsync(
        IReadOnlyCollection<StoreProductUpdate> updates,
        CancellationToken cancellationToken)
    {
        UpdateCalls.Add([.. updates]);
        if (UpdateFailure is { } failure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<StoreUpdateOutcome>>(failure));
        }

        IReadOnlyList<StoreUpdateOutcome> outcomes =
            [.. updates.Select(update => new StoreUpdateOutcome(update.Sku, ItemErrors.GetValueOrDefault(update.Sku)))];
        return Task.FromResult(Result.Success(outcomes));
    }
}

/// <summary>Fotos en memoria con el mismo comportamiento que el almacén real: confirmar crea o actualiza por SKU.</summary>
internal sealed class FakeSnapshotStore : IItemSnapshotStore
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    public Dictionary<string, ItemSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, ItemSnapshot>> FindAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, ItemSnapshot>>(
            skus.Where(Snapshots.ContainsKey).Distinct().ToDictionary(sku => sku, sku => Snapshots[sku], StringComparer.Ordinal));

    public Task ConfirmAsync(IReadOnlyCollection<SnapshotConfirmation> confirmations, CancellationToken cancellationToken)
    {
        foreach (var confirmation in confirmations)
        {
            if (Snapshots.TryGetValue(confirmation.Sku, out var existing))
            {
                existing.Confirm(confirmation.ItemCode, confirmation.StockQuantity, confirmation.Price, Now);
            }
            else
            {
                Snapshots[confirmation.Sku] =
                    ItemSnapshot.Create(confirmation.Sku, confirmation.ItemCode, confirmation.StockQuantity ?? 0, confirmation.Price, Now).Value;
            }
        }

        return Task.CompletedTask;
    }
}
