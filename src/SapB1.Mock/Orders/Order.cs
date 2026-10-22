using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Orders;

/// <summary>
/// Orden de venta (subconjunto del objeto <c>Orders</c> del Service Layer).
/// Al recibirla en un POST, el mock calcula <c>DocEntry</c>, <c>DocNum</c>, <c>CardName</c>,
/// los totales y los estados; lo que el cliente envíe en esas propiedades se ignora.
/// </summary>
internal sealed record Order
{
    /// <summary>Clave interna del documento; es la que usa la URL <c>Orders(n)</c>.</summary>
    public int DocEntry { get; init; }

    /// <summary>Número visible del documento, de la serie de numeración.</summary>
    public int DocNum { get; init; }

    public string DocType { get; init; } = DocumentTypes.Items;

    public DateOnly? DocDate { get; init; }

    public DateOnly? DocDueDate { get; init; }

    public string CardCode { get; init; } = string.Empty;

    public string? CardName { get; init; }

    /// <summary>Referencia del cliente (hasta 100 caracteres); el hub guarda aquí el id del pedido de la tienda.</summary>
    public string? NumAtCard { get; init; }

    /// <summary>Observaciones (hasta 254 caracteres).</summary>
    public string? Comments { get; init; }

    public string DocCurrency { get; init; } = "CLP";

    /// <summary>Suma de las líneas. El mock no calcula impuestos.</summary>
    public decimal DocTotal { get; init; }

    public string DocumentStatus { get; init; } = DocumentStatuses.Open;

    public string Cancelled { get; init; } = YesNo.No;

    public IReadOnlyList<DocumentLine> DocumentLines { get; init; } = [];
}

/// <summary>Línea de un documento de venta.</summary>
internal sealed record DocumentLine
{
    public int LineNum { get; init; }

    public string ItemCode { get; init; } = string.Empty;

    public string? ItemDescription { get; init; }

    /// <summary>Obligatoria y mayor que cero; es nula solo si el cliente no la envió.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Si el cliente no la envía, se usa el precio base del artículo.</summary>
    public decimal? UnitPrice { get; init; }

    public decimal LineTotal { get; init; }

    /// <summary>Si el cliente no la envía, se usa el almacén por defecto.</summary>
    public string? WarehouseCode { get; init; }

    public string LineStatus { get; init; } = DocumentStatuses.Open;
}

internal static class DocumentTypes
{
    public const string Items = "dDocument_Items";
}

/// <summary>Valores del enumerado <c>BoStatus</c> del Service Layer.</summary>
internal static class DocumentStatuses
{
    public const string Open = "bost_Open";
    public const string Closed = "bost_Close";
}
