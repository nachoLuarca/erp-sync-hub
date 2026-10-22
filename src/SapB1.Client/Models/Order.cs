namespace SapB1.Client.Models;

/// <summary>
/// Orden de venta (objeto <c>Orders</c>). Si la consulta usó <c>Select</c>,
/// las propiedades no pedidas quedan con su valor por defecto.
/// </summary>
public sealed record Order
{
    /// <summary>Clave interna del documento; es la que usa <see cref="IOrdersApi.GetAsync"/>.</summary>
    public int DocEntry { get; init; }

    /// <summary>Número visible del documento, de la serie de numeración.</summary>
    public int DocNum { get; init; }

    public string? DocType { get; init; }

    public DateOnly? DocDate { get; init; }

    public DateOnly? DocDueDate { get; init; }

    public string CardCode { get; init; } = string.Empty;

    public string? CardName { get; init; }

    /// <summary>Referencia del cliente (hasta 100 caracteres).</summary>
    public string? NumAtCard { get; init; }

    public string? Comments { get; init; }

    public string? DocCurrency { get; init; }

    public decimal DocTotal { get; init; }

    /// <summary>Ver <see cref="DocumentStatuses"/>.</summary>
    public string? DocumentStatus { get; init; }

    /// <summary><see cref="YesNo.Yes"/> si la orden fue cancelada.</summary>
    public string? Cancelled { get; init; }

    public IReadOnlyList<DocumentLine> DocumentLines { get; init; } = [];
}

/// <summary>Línea de un documento de venta.</summary>
public sealed record DocumentLine
{
    public int LineNum { get; init; }

    public string ItemCode { get; init; } = string.Empty;

    public string? ItemDescription { get; init; }

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public decimal LineTotal { get; init; }

    public string? WarehouseCode { get; init; }

    public string? LineStatus { get; init; }
}

/// <summary>Valores del enumerado <c>BoStatus</c>.</summary>
public static class DocumentStatuses
{
    public const string Open = "bost_Open";
    public const string Closed = "bost_Close";
}
