using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.Application.Inventory;

/// <summary>
/// Las fotos de lo último que la tienda confirmó por SKU (ver <see cref="ItemSnapshot"/>). Guardar es idempotente por SKU:
/// si ya hay foto se actualiza y si no se crea.
/// </summary>
public interface IItemSnapshotStore
{
    /// <summary>Las fotos de esos SKU. Solo contiene los que tienen foto.</summary>
    Task<IReadOnlyDictionary<string, ItemSnapshot>> FindAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    /// <summary>Registra lo que la tienda confirmó para cada producto.</summary>
    Task ConfirmAsync(IReadOnlyCollection<SnapshotConfirmation> confirmations, CancellationToken cancellationToken);
}

/// <summary>Lo que la tienda confirmó de un producto. Lo que es <see langword="null"/> no se envió y no cambia la foto.</summary>
/// <param name="Sku">SKU del producto.</param>
/// <param name="ItemCode">Artículo de SAP del que salieron los valores.</param>
/// <param name="StockQuantity">Stock confirmado.</param>
/// <param name="Price">Precio confirmado.</param>
public sealed record SnapshotConfirmation(string Sku, string ItemCode, int? StockQuantity, decimal? Price);
