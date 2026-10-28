namespace ErpSyncHub.Application.Sync;

/// <summary>
/// Consulta de los mapeos para el panel: listarlos y filtrarlos. Eliminar uno es de <see cref="IMappingStore"/>, que es lo que usa
/// también la sincronización cuando descubre un mapeo obsoleto.
/// </summary>
public interface IMappingAdmin
{
    /// <summary>Lista los mapeos SKU ↔ <c>ItemCode</c>, ordenados por SKU.</summary>
    Task<MappingPage<ProductMappingInfo>> ListProductsAsync(ProductMappingFilter filter, CancellationToken cancellationToken);

    /// <summary>Lista los mapeos cliente ↔ <c>CardCode</c>, del más reciente al más antiguo.</summary>
    Task<MappingPage<CustomerMappingInfo>> ListCustomersAsync(CustomerMappingFilter filter, CancellationToken cancellationToken);
}

/// <summary>Un mapeo de producto.</summary>
public sealed record ProductMappingInfo(string Sku, string ItemCode, DateTimeOffset CreatedAt);

/// <summary>Un mapeo de cliente. La clave de un invitado es su correo y el <c>CardCode</c> se deriva del RUT: ambos son datos personales.</summary>
public sealed record CustomerMappingInfo(string Key, string CardCode, DateTimeOffset CreatedAt);

/// <summary>Los tipos de cliente que distingue el filtro.</summary>
public enum CustomerKeyKind
{
    /// <summary>Cliente con cuenta en la tienda (<c>customer:4521</c>).</summary>
    Registered,

    /// <summary>Invitado, identificado por su correo (<c>email:ana@example.com</c>).</summary>
    Guest,
}

/// <summary>Filtros del listado de mapeos de producto.</summary>
/// <param name="Sku">Contiene este texto, sin distinguir mayúsculas.</param>
/// <param name="ItemCode">Contiene este texto, sin distinguir mayúsculas.</param>
/// <param name="Page">Número de página, desde 1.</param>
/// <param name="PageSize">Mapeos por página.</param>
public sealed record ProductMappingFilter(string? Sku, string? ItemCode, int Page, int PageSize);

/// <summary>Filtros del listado de mapeos de cliente.</summary>
/// <param name="Key">La clave exacta del cliente.</param>
/// <param name="CardCode">El <c>CardCode</c> exacto.</param>
/// <param name="Kind">Solo clientes registrados o solo invitados.</param>
/// <param name="Page">Número de página, desde 1.</param>
/// <param name="PageSize">Mapeos por página.</param>
public sealed record CustomerMappingFilter(string? Key, string? CardCode, CustomerKeyKind? Kind, int Page, int PageSize);

/// <summary>Una página de mapeos.</summary>
public sealed record MappingPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems)
{
    /// <summary>Cantidad de páginas con el tamaño actual.</summary>
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
}
