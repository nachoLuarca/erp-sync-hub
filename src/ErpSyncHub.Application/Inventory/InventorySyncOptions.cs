namespace ErpSyncHub.Application.Inventory;

/// <summary>
/// Cómo se sincronizan el stock y los precios de SAP hacia la tienda (sección <c>Sync:Inventory</c>, variables
/// <c>Sync__Inventory__*</c>).
/// </summary>
public sealed class InventorySyncOptions
{
    public const string SectionName = "Sync:Inventory";

    /// <summary>Días que se retrocede la fecha de corte al leer SAP: <c>UpdateDate</c> no trae hora ni zona horaria.</summary>
    public const int OverlapDays = 1;

    /// <summary>
    /// Permite apagar la sincronización periódica. Aun encendida, solo corre si la tienda está configurada. La sincronización
    /// manual del panel no depende de este valor.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cada cuánto corre la sincronización.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Códigos de los almacenes de SAP cuyo stock se publica, separados por comas (por ejemplo <c>01,02</c>). El stock
    /// disponible es la suma de lo que hay en mano menos lo comprometido en ellos.
    /// </summary>
    public string Warehouses { get; set; } = "01";

    /// <summary>Número de la lista de precios de SAP de la que sale el precio de la tienda.</summary>
    public int PriceList { get; set; } = 1;

    /// <summary>Decimales con que se envía el precio. Cero para pesos chilenos.</summary>
    public int PriceDecimals { get; set; }

    /// <summary>Artículos que se leen de SAP por página.</summary>
    public int SapPageSize { get; set; } = 100;

    /// <summary>
    /// Zona horaria de SAP (identificador IANA), para convertir el instante de una ejecución en la fecha local de SAP con
    /// que se filtra <c>UpdateDate</c>.
    /// </summary>
    public string TimeZoneId { get; set; } = "America/Santiago";

    /// <summary>Los códigos de <see cref="Warehouses"/>, sin espacios ni repetidos.</summary>
    public IReadOnlyList<string> WarehouseCodes =>
        [.. Warehouses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
}
