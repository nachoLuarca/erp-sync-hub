namespace ErpSyncHub.Application.WooCommerce;

/// <summary>
/// Un pedido de WooCommerce, con solo los datos que el hub necesita (el JSON original completo queda guardado en el job).
/// Los montos están en la moneda del pedido.
/// </summary>
/// <param name="Id">Identificador del pedido en la tienda.</param>
/// <param name="Number">Número visible del pedido.</param>
/// <param name="Status">Estado en minúsculas: <c>pending</c>, <c>processing</c>, <c>completed</c>…</param>
/// <param name="Currency">Código de moneda ISO.</param>
/// <param name="Total">Total del pedido con envío e impuestos.</param>
/// <param name="TotalTax">Impuestos del pedido, incluidos los del envío.</param>
/// <param name="ShippingTotal">Costo de envío sin impuestos.</param>
/// <param name="DiscountTotal">Descuentos aplicados por cupones, sin impuestos.</param>
/// <param name="CustomerId">Identificador del cliente registrado, o 0 si compró como invitado.</param>
/// <param name="CustomerNote">Nota que dejó el cliente.</param>
/// <param name="DateCreated">Cuándo se creó el pedido, en UTC.</param>
/// <param name="Billing">Datos de facturación.</param>
/// <param name="Shipping">Datos de despacho.</param>
/// <param name="LineItems">Productos del pedido.</param>
/// <param name="MetaData">Metadatos del pedido (por ejemplo, el RUT que agrega un plugin), por clave.</param>
public sealed record WooCommerceOrder(
    long Id,
    string Number,
    string Status,
    string Currency,
    decimal Total,
    decimal TotalTax,
    decimal ShippingTotal,
    decimal DiscountTotal,
    long CustomerId,
    string? CustomerNote,
    DateTimeOffset? DateCreated,
    WooCommerceAddress Billing,
    WooCommerceAddress Shipping,
    IReadOnlyList<WooCommerceLineItem> LineItems,
    IReadOnlyDictionary<string, string> MetaData)
{
    /// <summary>Indica si el cliente es un invitado, sin cuenta en la tienda.</summary>
    public bool IsGuest => CustomerId == 0;

    /// <summary>Valor de un metadato del pedido, o <see langword="null"/> si no existe o está vacío.</summary>
    public string? GetMeta(string key) =>
        MetaData.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>Dirección de facturación o de despacho.</summary>
public sealed record WooCommerceAddress(
    string FirstName,
    string LastName,
    string Company,
    string Address1,
    string Address2,
    string City,
    string State,
    string Postcode,
    string Country,
    string Email,
    string Phone)
{
    /// <summary>Nombre completo de la persona, o la empresa si no hay nombre.</summary>
    public string FullName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            return name.Length > 0 ? name : Company;
        }
    }
}

/// <summary>Un producto del pedido.</summary>
/// <param name="Id">Identificador de la línea.</param>
/// <param name="ProductId">Identificador del producto en la tienda.</param>
/// <param name="VariationId">Identificador de la variación, o 0 si no es un producto variable.</param>
/// <param name="Name">Nombre del producto.</param>
/// <param name="Sku">SKU del producto o de su variación; puede venir vacío.</param>
/// <param name="Quantity">Cantidad pedida.</param>
/// <param name="Subtotal">Total de la línea antes de descuentos, sin impuestos.</param>
/// <param name="Total">Total de la línea después de descuentos, sin impuestos.</param>
/// <param name="TotalTax">Impuestos de la línea.</param>
public sealed record WooCommerceLineItem(
    long Id,
    long ProductId,
    long VariationId,
    string Name,
    string Sku,
    decimal Quantity,
    decimal Subtotal,
    decimal Total,
    decimal TotalTax);
