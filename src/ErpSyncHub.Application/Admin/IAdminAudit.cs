using ErpSyncHub.Domain.Admin;

namespace ErpSyncHub.Application.Admin;

/// <summary>
/// El registro de auditoría de las acciones manuales del panel. Es de solo inserción: el puerto no ofrece ninguna operación para
/// modificar ni borrar entradas, y la base de datos lo impide con un disparador.
/// </summary>
public interface IAdminAudit
{
    /// <summary>Agrega una entrada.</summary>
    Task RecordAsync(AdminAuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Lista las entradas, de la más reciente a la más antigua.</summary>
    Task<AdminAuditPage> ListAsync(AdminAuditFilter filter, CancellationToken cancellationToken);
}

/// <summary>Filtros del listado de auditoría.</summary>
/// <param name="Action">Solo esa acción (por ejemplo <c>job.retry</c>).</param>
/// <param name="KeyFingerprint">Solo las acciones hechas con esa clave.</param>
/// <param name="From">Desde esa fecha (inclusive).</param>
/// <param name="To">Hasta esa fecha (exclusive).</param>
/// <param name="Page">Número de página, desde 1.</param>
/// <param name="PageSize">Entradas por página.</param>
public sealed record AdminAuditFilter(string? Action, string? KeyFingerprint, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize);

/// <summary>Una página del listado de auditoría.</summary>
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditEntry> Items, int Page, int PageSize, int TotalItems)
{
    /// <summary>Cantidad de páginas con el tamaño actual.</summary>
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
}
