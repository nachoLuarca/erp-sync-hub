namespace SapB1.Client.Models;

/// <summary>Datos para crear una orden de venta.</summary>
public sealed record CreateOrderRequest
{
    /// <summary>Cliente de la orden. Debe existir, estar activo y no ser un proveedor.</summary>
    public required string CardCode { get; init; }

    /// <summary>Fecha del documento. Si se omite, SAP usa la fecha de hoy.</summary>
    public DateOnly? DocDate { get; init; }

    /// <summary>Fecha de entrega. Si se omite, SAP usa <see cref="DocDate"/>.</summary>
    public DateOnly? DocDueDate { get; init; }

    /// <summary>
    /// Referencia del cliente, hasta 100 caracteres. Útil para guardar el identificador del pedido de origen
    /// y poder detectar duplicados con <see cref="SapFilter.Equal(string, string)"/>.
    /// </summary>
    public string? NumAtCard { get; init; }

    /// <summary>Observaciones, hasta 254 caracteres.</summary>
    public string? Comments { get; init; }

    /// <summary>Al menos una línea.</summary>
    public required IReadOnlyList<CreateOrderLine> DocumentLines { get; init; }
}

/// <summary>Línea de una orden de venta nueva.</summary>
public sealed record CreateOrderLine
{
    public required string ItemCode { get; init; }

    /// <summary>Mayor que cero.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Si se omite, SAP usa el precio base del artículo.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>Si se omite, SAP usa el almacén por defecto.</summary>
    public string? WarehouseCode { get; init; }
}
