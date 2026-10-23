namespace ErpSyncHub.Application.WooCommerce;

/// <summary>Configuración del webhook de WooCommerce (sección <c>Webhooks:WooCommerce</c>).</summary>
public sealed class WooCommerceWebhookOptions
{
    public const string SectionName = "Webhooks:WooCommerce";

    /// <summary>Menor tamaño de secreto que se acepta: es la clave de la firma y debe ser difícil de adivinar.</summary>
    public const int MinimumSecretLength = 16;

    /// <summary>Estados de pedido que se sincronizan por defecto: los que ya están pagados.</summary>
    public static readonly IReadOnlyList<string> DefaultAcceptedStatuses = ["processing", "completed"];

    /// <summary>Topics de WooCommerce que traen un pedido completo.</summary>
    public static readonly IReadOnlyList<string> OrderTopics = ["order.created", "order.updated"];

    /// <summary>
    /// Secreto compartido con la tienda para firmar los webhooks. Debe venir de una variable de entorno
    /// (<c>Webhooks__WooCommerce__Secret</c>) y ser el mismo que se configuró en el webhook de WooCommerce.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Estados de pedido que se encolan. Los demás se responden con 200 y se ignoran, porque encolar un pedido sin
    /// pagar crearía una orden de venta en SAP por algo que aún no se vendió. Vacío significa los de
    /// <see cref="DefaultAcceptedStatuses"/>.
    /// </summary>
    public List<string> AcceptedStatuses { get; set; } = [];

    /// <summary>Tamaño máximo del cuerpo que se acepta, en bytes. Por defecto, 1 MiB.</summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>Estados efectivos, en minúsculas.</summary>
    public IReadOnlySet<string> EffectiveAcceptedStatuses() =>
        (AcceptedStatuses.Count > 0 ? AcceptedStatuses : [.. DefaultAcceptedStatuses])
            .Select(status => status.Trim().ToLowerInvariant())
            .Where(status => status.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}
