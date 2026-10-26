using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Inventory;

/// <summary>
/// Fotos de lo último confirmado en la tienda, en PostgreSQL. Confirmar es un <c>upsert</c> por SKU: si dos instancias
/// escribieran a la vez, la restricción única decide y la segunda actualiza en vez de duplicar.
/// </summary>
internal sealed class ItemSnapshotStore(ErpSyncHubDbContext context, TimeProvider time) : IItemSnapshotStore
{
    public async Task<IReadOnlyDictionary<string, ItemSnapshot>> FindAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken)
    {
        if (skus.Count == 0)
        {
            return new Dictionary<string, ItemSnapshot>();
        }

        var keys = skus.Distinct(StringComparer.Ordinal).ToArray();
        var found = await context.ItemSnapshots
            .AsNoTracking()
            .Where(snapshot => keys.Contains(snapshot.Sku))
            .ToListAsync(cancellationToken);

        return found.ToDictionary(snapshot => snapshot.Sku, StringComparer.Ordinal);
    }

    public async Task ConfirmAsync(IReadOnlyCollection<SnapshotConfirmation> confirmations, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        foreach (var confirmation in confirmations)
        {
            var created = ItemSnapshot.Create(
                confirmation.Sku,
                confirmation.ItemCode,
                confirmation.StockQuantity ?? 0,
                confirmation.Price,
                now);
            if (created.IsFailure)
            {
                throw new ArgumentException(created.Error!.Message, nameof(confirmations));
            }

            var value = created.Value;

            // Al insertar, lo que no se envió queda en su valor inicial; al actualizar, se conserva lo que ya había.
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO item_snapshots (id, sku, item_code, stock_quantity, price, synced_at)
                VALUES ({value.Id}, {value.Sku}, {value.ItemCode}, {value.StockQuantity}, {value.Price}, {value.SyncedAt})
                ON CONFLICT (sku) DO UPDATE SET
                    item_code = EXCLUDED.item_code,
                    stock_quantity = CASE WHEN {confirmation.StockQuantity is not null} THEN EXCLUDED.stock_quantity ELSE item_snapshots.stock_quantity END,
                    price = CASE WHEN {confirmation.Price is not null} THEN EXCLUDED.price ELSE item_snapshots.price END,
                    synced_at = EXCLUDED.synced_at
                """,
                cancellationToken);
        }
    }
}
