using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Domain.Mappings;

/// <summary>Equivalencia entre el SKU de un producto de la tienda y el <c>ItemCode</c> del artículo en SAP.</summary>
public sealed class ProductMapping
{
    public const int MaxSkuLength = 100;
    public const int MaxItemCodeLength = 50;

    // Para EF Core.
    private ProductMapping()
    {
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string ItemCode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<ProductMapping> Create(string sku, string itemCode, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sku) || sku.Trim().Length > MaxSkuLength)
        {
            return Result.Failure<ProductMapping>(
                ResultError.Validation("mapping.invalid_sku", $"El SKU es obligatorio y admite hasta {MaxSkuLength} caracteres."));
        }

        if (string.IsNullOrWhiteSpace(itemCode) || itemCode.Trim().Length > MaxItemCodeLength)
        {
            return Result.Failure<ProductMapping>(
                ResultError.Validation("mapping.invalid_item_code", $"El ItemCode es obligatorio y admite hasta {MaxItemCodeLength} caracteres."));
        }

        return Result.Success(new ProductMapping
        {
            Id = Guid.CreateVersion7(now),
            Sku = sku.Trim(),
            ItemCode = itemCode.Trim(),
            CreatedAt = now,
        });
    }
}
