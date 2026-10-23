namespace ErpSyncHub.Application.Sync;

/// <summary>Cómo se convierte un pedido de WooCommerce en una orden de SAP (sección <c>Sync:WooCommerce</c>).</summary>
public sealed class OrderSyncOptions
{
    public const string SectionName = "Sync:WooCommerce";

    /// <summary>Prefijo de la referencia del cliente (<c>NumAtCard</c>) con la que se reconoce una orden ya creada.</summary>
    public const string ReferencePrefix = "WC-";

    /// <summary>
    /// Clave del metadato del pedido donde un plugin de WooCommerce guarda el RUT del cliente. Si el pedido no trae ese
    /// metadato, el cliente se identifica por su correo. Vacío desactiva el uso del RUT.
    /// </summary>
    public string TaxIdMetaKey { get; set; } = "_billing_rut";

    /// <summary>
    /// Zona horaria de la tienda (identificador IANA, por ejemplo <c>America/Santiago</c>), para fechar la orden con el día
    /// en que el cliente compró y no con el día UTC.
    /// </summary>
    public string TimeZoneId { get; set; } = "America/Santiago";
}
