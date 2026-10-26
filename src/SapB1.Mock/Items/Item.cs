namespace SapB1.Mock.Items;

/// <summary>
/// Artículo del maestro de SAP (subconjunto del objeto <c>Items</c> del Service Layer). <c>UpdateDate</c> y
/// <c>UpdateTime</c> son la fecha y la hora locales de SAP de la última modificación, sin zona horaria.
/// </summary>
internal sealed record Item(
    string ItemCode,
    string ItemName,
    string Valid,
    string Frozen,
    DateOnly UpdateDate,
    TimeOnly UpdateTime,
    IReadOnlyList<ItemPrice> ItemPrices,
    IReadOnlyList<ItemWarehouseInfo> ItemWarehouseInfoCollection)
{
    /// <summary>Stock total: suma de <c>InStock</c> de todos los almacenes.</summary>
    public decimal QuantityOnStock => ItemWarehouseInfoCollection.Sum(w => w.InStock);
}

/// <summary>Precio del artículo en una lista de precios.</summary>
internal sealed record ItemPrice(int PriceList, decimal Price, string Currency);

/// <summary>Existencias del artículo en un almacén.</summary>
internal sealed record ItemWarehouseInfo(string WarehouseCode, decimal InStock, decimal Committed, decimal Ordered);
