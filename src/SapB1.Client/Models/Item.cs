namespace SapB1.Client.Models;

/// <summary>
/// Artículo del maestro de SAP (objeto <c>Items</c>). Si la consulta usó <c>Select</c>,
/// las propiedades no pedidas quedan con su valor por defecto.
/// </summary>
public sealed record Item
{
    public string ItemCode { get; init; } = string.Empty;

    public string? ItemName { get; init; }

    /// <summary>Habilitado: <see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Valid { get; init; }

    /// <summary>Congelado: <see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Frozen { get; init; }

    /// <summary>Stock total, suma de todos los almacenes.</summary>
    public decimal QuantityOnStock { get; init; }

    public IReadOnlyList<ItemPrice> ItemPrices { get; init; } = [];

    public IReadOnlyList<ItemWarehouseInfo> ItemWarehouseInfoCollection { get; init; } = [];
}

/// <summary>Precio del artículo en una lista de precios.</summary>
public sealed record ItemPrice
{
    public int PriceList { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }
}

/// <summary>Existencias del artículo en un almacén.</summary>
public sealed record ItemWarehouseInfo
{
    public string WarehouseCode { get; init; } = string.Empty;

    public decimal InStock { get; init; }

    /// <summary>Comprometido en órdenes de venta aún no entregadas.</summary>
    public decimal Committed { get; init; }

    /// <summary>Pedido a proveedores.</summary>
    public decimal Ordered { get; init; }
}
