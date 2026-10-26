using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Domain.Inventory;

/// <summary>
/// La última foto de un producto que el hub dejó confirmada en la tienda: el stock y el precio que ya tiene. Comparar lo que
/// SAP informa contra esta foto, y no contra la tienda, permite enviar solo lo que cambió sin consultar la tienda cada vez.
/// Hay una foto por SKU, porque la tienda se identifica por SKU y varios SKU pueden apuntar al mismo artículo de SAP.
/// </summary>
public sealed class ItemSnapshot
{
    public const int MaxSkuLength = 100;
    public const int MaxItemCodeLength = 50;

    // Para EF Core.
    private ItemSnapshot()
    {
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string ItemCode { get; private set; } = string.Empty;

    /// <summary>Stock disponible que tiene la tienda.</summary>
    public int StockQuantity { get; private set; }

    /// <summary>Precio que tiene la tienda; <see langword="null"/> si el hub nunca le ha enviado uno.</summary>
    public decimal? Price { get; private set; }

    public DateTimeOffset SyncedAt { get; private set; }

    public static Result<ItemSnapshot> Create(string sku, string itemCode, int stockQuantity, decimal? price, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sku) || sku.Length > MaxSkuLength)
        {
            return Result.Failure<ItemSnapshot>(
                ResultError.Validation("snapshot.invalid_sku", $"El SKU es obligatorio y admite hasta {MaxSkuLength} caracteres."));
        }

        if (string.IsNullOrWhiteSpace(itemCode) || itemCode.Length > MaxItemCodeLength)
        {
            return Result.Failure<ItemSnapshot>(
                ResultError.Validation("snapshot.invalid_item_code", $"El ItemCode es obligatorio y admite hasta {MaxItemCodeLength} caracteres."));
        }

        if (stockQuantity < 0)
        {
            return Result.Failure<ItemSnapshot>(
                ResultError.Validation("snapshot.invalid_stock", "El stock no puede ser negativo."));
        }

        return Result.Success(new ItemSnapshot
        {
            Id = Guid.CreateVersion7(now),
            Sku = sku,
            ItemCode = itemCode,
            StockQuantity = stockQuantity,
            Price = price,
            SyncedAt = now,
        });
    }

    /// <summary>
    /// Registra lo que la tienda confirmó. Lo que es <see langword="null"/> no se envió y se conserva. Si el SKU ahora
    /// pertenece a otro artículo de SAP, la foto lo sigue.
    /// </summary>
    public void Confirm(string itemCode, int? stockQuantity, decimal? price, DateTimeOffset now)
    {
        ItemCode = itemCode;
        StockQuantity = stockQuantity ?? StockQuantity;
        Price = price ?? Price;
        SyncedAt = now;
    }
}
