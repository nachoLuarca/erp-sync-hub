using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Application.Inventory;

/// <summary>Qué leer de SAP en una ejecución.</summary>
/// <param name="ChangedSince">Fecha de SAP desde la que leer (inclusive); <see langword="null"/> lee todo el maestro.</param>
/// <param name="Full">Si es verdadero, además de leer todo se ignora la foto y se envía todo a la tienda.</param>
public sealed record InventorySyncRequest(DateOnly? ChangedSince, bool Full = false);

/// <summary>Resultado de una ejecución de <see cref="InventorySyncService"/>.</summary>
/// <param name="Counts">Qué pasó con cada artículo leído. Siempre suman lo leído.</param>
/// <param name="Error">Por qué se interrumpió; <see langword="null"/> si recorrió todo lo que debía.</param>
public sealed record InventorySyncReport(InventorySyncCounts Counts, ResultError? Error)
{
    /// <summary>Verdadero si la ejecución no se interrumpió (aunque algunos artículos hayan fallado).</summary>
    public bool Completed => Error is null;
}

/// <summary>
/// Lleva stock y precio de SAP a la tienda (ADR 0009). Por cada página del maestro de artículos:
/// <list type="number">
/// <item>Calcula lo que corresponde publicar: stock disponible (en mano menos comprometido, de los almacenes configurados,
/// con tope en 0; 0 si el artículo está inhabilitado) y el precio de la lista configurada (sin precio válido, no se envía).</item>
/// <item>Resuelve los SKU: los mapeos guardados, o el <c>ItemCode</c> como SKU si el artículo no tiene mapeo.</item>
/// <item>Descarta lo que no cambió respecto de la última foto confirmada en la tienda.</item>
/// <item>Busca los productos por SKU y les envía el cambio por lotes; lo que no existe en la tienda se omite.</item>
/// <item>Guarda la foto de lo que la tienda confirmó. Lo que falla no se guarda: la siguiente ejecución lo reintenta.</item>
/// </list>
/// Una falla de la tienda que afecta a todo (credenciales, caída) interrumpe la ejecución.
/// </summary>
public sealed partial class InventorySyncService(
    ISapGateway sap,
    IStoreGateway store,
    IMappingStore mappings,
    IItemSnapshotStore snapshots,
    InventorySyncOptions options,
    ILogger<InventorySyncService> logger)
{
    private const int SampleSize = 20;

    // El orden importa: un artículo con varios SKU toma el resultado más grave.
    private enum ItemResult
    {
        Unchanged,
        Skipped,
        Updated,
        Failed,
    }

    public async Task<InventorySyncReport> SyncAsync(InventorySyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = new RunState();
        var changedSince = request.Full ? null : request.ChangedSince;
        string? after = null;
        ResultError? error = null;

        while (true)
        {
            var page = await sap.GetInventoryPageAsync(changedSince, after, options.SapPageSize, cancellationToken);
            if (page.IsFailure)
            {
                error = page.Error;
                break;
            }

            if (page.Value.Count == 0)
            {
                break;
            }

            error = await ProcessPageAsync(page.Value, request.Full, run, cancellationToken);
            if (error is not null || page.Value.Count < options.SapPageSize)
            {
                break;
            }

            after = page.Value[^1].ItemCode;
        }

        LogNotes(run);
        return new InventorySyncReport(run.ToCounts(), error);
    }

    /// <summary>Procesa una página. Devuelve el error que interrumpe la ejecución, o <see langword="null"/> si debe seguir.</summary>
    private async Task<ResultError?> ProcessPageAsync(
        IReadOnlyList<SapInventoryItem> items,
        bool full,
        RunState run,
        CancellationToken cancellationToken)
    {
        var results = items.ToDictionary(item => item.ItemCode, _ => ItemResult.Unchanged, StringComparer.Ordinal);
        var targets = await BuildTargetsAsync(items, cancellationToken);
        var known = await snapshots.FindAsync([.. targets.Select(target => target.Sku)], cancellationToken);

        var pending = new List<Target>();
        foreach (var target in targets)
        {
            var snapshot = known.GetValueOrDefault(target.Sku);
            var change = target.Desired.ChangeAgainst(snapshot, full);

            if (target.Desired.PriceInvalid && !run.InvalidPrice.Contains(target.Desired.Item.ItemCode))
            {
                run.InvalidPrice.Add(target.Desired.Item.ItemCode);
            }

            if (change is null)
            {
                Raise(results, target.Desired.Item.ItemCode, target.Desired.PriceInvalid ? ItemResult.Skipped : ItemResult.Unchanged);
            }
            else
            {
                pending.Add(target with { Change = change });
            }
        }

        ResultError? abort = null;
        if (pending.Count > 0)
        {
            abort = await PublishAsync(pending, results, run, cancellationToken);
        }

        run.Add(results.Values);
        return abort;
    }

    private async Task<List<Target>> BuildTargetsAsync(IReadOnlyList<SapInventoryItem> items, CancellationToken cancellationToken)
    {
        var mapped = await mappings.FindSkusAsync([.. items.Select(item => item.ItemCode)], cancellationToken);
        var targets = new List<Target>(items.Count);

        foreach (var item in items)
        {
            var desired = Desired.From(item, options);
            if (mapped.TryGetValue(item.ItemCode, out var skus) && skus.Count > 0)
            {
                targets.AddRange(skus.Select(sku => new Target(desired, sku, ViaFallback: false, Change: null)));
            }
            else
            {
                // Sin mapeo, se asume que el SKU de la tienda es el código del artículo, igual que al sincronizar pedidos.
                targets.Add(new Target(desired, item.ItemCode, ViaFallback: true, Change: null));
            }
        }

        return targets;
    }

    private async Task<ResultError?> PublishAsync(
        List<Target> pending,
        Dictionary<string, ItemResult> results,
        RunState run,
        CancellationToken cancellationToken)
    {
        var found = await store.FindProductsBySkuAsync([.. pending.Select(target => target.Sku).Distinct(StringComparer.Ordinal)], cancellationToken);
        if (found.IsFailure)
        {
            FailAll(pending, results, run, found.Error!);
            return found.Error;
        }

        var updates = new List<StoreProductUpdate>(pending.Count);
        var byUpdateSku = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var target in pending)
        {
            if (!found.Value.TryGetValue(target.Sku, out var product))
            {
                run.NoProduct.Add(target.Sku);
                Raise(results, target.Desired.Item.ItemCode, ItemResult.Skipped);
                continue;
            }

            if (target.ViaFallback)
            {
                await mappings.SaveProductMappingAsync(target.Sku, target.Desired.Item.ItemCode, cancellationToken);
            }

            updates.Add(new StoreProductUpdate(product.Id, target.Sku, target.Change!.Stock, target.Change.Price));
            byUpdateSku[target.Sku] = target;
        }

        if (updates.Count == 0)
        {
            return null;
        }

        var updated = await store.UpdateProductsAsync(updates, cancellationToken);
        if (updated.IsFailure)
        {
            FailAll([.. byUpdateSku.Values], results, run, updated.Error!);
            return updated.Error;
        }

        var confirmations = new List<SnapshotConfirmation>();
        ResultError? interruption = null;
        foreach (var outcome in updated.Value)
        {
            var target = byUpdateSku[outcome.Sku];
            var itemCode = target.Desired.Item.ItemCode;

            if (outcome.Error is { } failure)
            {
                run.Failed.Add($"{outcome.Sku}: {failure.Message}");
                Raise(results, itemCode, ItemResult.Failed);
                if (failure.IsTransient)
                {
                    interruption ??= failure;
                }

                continue;
            }

            Raise(results, itemCode, ItemResult.Updated);
            confirmations.Add(new SnapshotConfirmation(outcome.Sku, itemCode, target.Change!.Stock, target.Change.Price));
        }

        // Lo que la tienda no devolvió se da por no confirmado: la siguiente ejecución lo reintenta.
        foreach (var missing in byUpdateSku.Keys.Except(updated.Value.Select(outcome => outcome.Sku), StringComparer.Ordinal))
        {
            run.Failed.Add($"{missing}: la tienda no informó el resultado.");
            Raise(results, byUpdateSku[missing].Desired.Item.ItemCode, ItemResult.Failed);
        }

        if (confirmations.Count > 0)
        {
            await snapshots.ConfirmAsync(confirmations, cancellationToken);
        }

        return interruption;
    }

    private static void FailAll(List<Target> targets, Dictionary<string, ItemResult> results, RunState run, ResultError error)
    {
        foreach (var target in targets)
        {
            run.Failed.Add($"{target.Sku}: {error.Message}");
            Raise(results, target.Desired.Item.ItemCode, ItemResult.Failed);
        }
    }

    private static void Raise(Dictionary<string, ItemResult> results, string itemCode, ItemResult result)
    {
        if (result > results[itemCode])
        {
            results[itemCode] = result;
        }
    }

    private void LogNotes(RunState run)
    {
        if (run.NoProduct.Count > 0)
        {
            LogNoProduct(run.NoProduct.Count, Sample(run.NoProduct));
        }

        if (run.InvalidPrice.Count > 0)
        {
            LogInvalidPrice(run.InvalidPrice.Count, options.PriceList, Sample(run.InvalidPrice));
        }

        if (run.Failed.Count > 0)
        {
            LogFailed(run.Failed.Count, Sample(run.Failed));
        }
    }

    private static string Sample(List<string> values)
    {
        var shown = string.Join("; ", values.Take(SampleSize));
        return values.Count > SampleSize ? $"{shown}; y {values.Count - SampleSize} más" : shown;
    }

    /// <summary>Los valores que corresponde publicar de un artículo, ya calculados.</summary>
    private sealed record Desired(SapInventoryItem Item, int Stock, decimal? Price, bool PriceInvalid)
    {
        public static Desired From(SapInventoryItem item, InventorySyncOptions options)
        {
            var warehouses = options.WarehouseCodes;
            var available = item.Warehouses
                .Where(warehouse => warehouses.Contains(warehouse.WarehouseCode, StringComparer.Ordinal))
                .Sum(warehouse => warehouse.InStock - warehouse.Committed);

            // Un artículo inhabilitado o congelado queda sin stock pero publicado.
            var stock = item.IsActive
                ? (int)Math.Min(int.MaxValue, Math.Max(0m, Math.Floor(available)))
                : 0;

            var listed = item.Prices.FirstOrDefault(price => price.PriceList == options.PriceList)?.Price;
            return listed is > 0
                ? new Desired(item, stock, Math.Round(listed.Value, options.PriceDecimals, MidpointRounding.AwayFromZero), PriceInvalid: false)
                : new Desired(item, stock, Price: null, PriceInvalid: true);
        }

        /// <summary>Lo que hay que enviar para que la tienda quede al día, o <see langword="null"/> si ya lo está.</summary>
        public Change? ChangeAgainst(ItemSnapshot? snapshot, bool full)
        {
            int? stock = full || snapshot is null || snapshot.StockQuantity != Stock ? Stock : null;
            decimal? price = Price is { } desired && (full || snapshot?.Price is not { } known || known != desired) ? desired : null;

            return stock is null && price is null ? null : new Change(stock, price);
        }
    }

    private sealed record Change(int? Stock, decimal? Price);

    private sealed record Target(Desired Desired, string Sku, bool ViaFallback, Change? Change);

    private sealed class RunState
    {
        private int _read;
        private int _updated;
        private int _unchanged;
        private int _skipped;
        private int _failed;

        public List<string> NoProduct { get; } = [];

        public List<string> InvalidPrice { get; } = [];

        public List<string> Failed { get; } = [];

        public void Add(IEnumerable<ItemResult> results)
        {
            foreach (var result in results)
            {
                _read++;
                switch (result)
                {
                    case ItemResult.Updated:
                        _updated++;
                        break;
                    case ItemResult.Unchanged:
                        _unchanged++;
                        break;
                    case ItemResult.Skipped:
                        _skipped++;
                        break;
                    default:
                        _failed++;
                        break;
                }
            }
        }

        public InventorySyncCounts ToCounts() => new(_read, _updated, _unchanged, _skipped, _failed);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} artículos de SAP no tienen producto en la tienda y se omitieron (crea el producto con ese SKU o define el mapeo): {Skus}")]
    private partial void LogNoProduct(int count, string skus);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} artículos no tienen un precio válido (nulo o menor o igual a 0) en la lista {PriceList} de SAP; no se envió su precio: {ItemCodes}")]
    private partial void LogInvalidPrice(int count, int priceList, string itemCodes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} productos no se pudieron actualizar en la tienda; se reintentarán en la próxima ejecución: {Details}")]
    private partial void LogFailed(int count, string details);
}
