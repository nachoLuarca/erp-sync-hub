namespace ErpSyncHub.Application.Sync;

/// <summary>
/// Equivalencias entre la tienda y SAP: SKU ↔ <c>ItemCode</c> y cliente de la tienda ↔ <c>CardCode</c>.
/// Guardarlas evita consultar a SAP en cada pedido. Las escrituras son idempotentes: si la clave ya existe no se
/// modifica, de modo que reprocesar un pedido nunca pisa un mapeo.
/// </summary>
public interface IMappingStore
{
    /// <summary>El <c>ItemCode</c> asociado al SKU, o <see langword="null"/> si no hay mapeo.</summary>
    Task<string?> FindItemCodeAsync(string sku, CancellationToken cancellationToken);

    /// <summary>El <c>CardCode</c> asociado al cliente (ver <c>CustomerKey</c>), o <see langword="null"/> si no hay mapeo.</summary>
    Task<string?> FindCardCodeAsync(string customerKey, CancellationToken cancellationToken);

    /// <summary>Guarda el mapeo del producto si el SKU aún no tiene uno.</summary>
    Task SaveProductMappingAsync(string sku, string itemCode, CancellationToken cancellationToken);

    /// <summary>Guarda el mapeo del cliente si aún no tiene uno.</summary>
    Task SaveCustomerMappingAsync(string customerKey, string cardCode, CancellationToken cancellationToken);
}
