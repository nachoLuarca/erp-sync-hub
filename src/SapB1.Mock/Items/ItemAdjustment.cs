namespace SapB1.Mock.Items;

/// <summary>
/// Cambio manual al maestro de artículos, solo para pruebas (<c>PATCH /_mock/items/{itemCode}</c>). Los campos
/// omitidos no se tocan.
/// </summary>
/// <param name="WarehouseCode">Almacén al que aplican <paramref name="InStock"/> y <paramref name="Committed"/>; por defecto el principal.</param>
/// <param name="InStock">Nuevo stock físico del almacén.</param>
/// <param name="Committed">Nuevo stock comprometido del almacén.</param>
/// <param name="PriceList">Lista de precios a la que aplica <paramref name="Price"/>; por defecto la base.</param>
/// <param name="Price">Nuevo precio en la lista.</param>
/// <param name="Frozen">Congela o descongela el artículo.</param>
internal sealed record ItemAdjustment(
    string? WarehouseCode,
    decimal? InStock,
    decimal? Committed,
    int? PriceList,
    decimal? Price,
    bool? Frozen)
{
    /// <summary>Devuelve el motivo por el que el cambio no es válido, o <see langword="null"/> si lo es.</summary>
    public string? Validate() =>
        InStock is < 0 ? "InStock no puede ser negativo."
        : Committed is < 0 ? "Committed no puede ser negativo."
        : Price is < 0 ? "Price no puede ser negativo."
        : null;
}
