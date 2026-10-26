using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Application.Jobs;

/// <summary>
/// Lo que el panel necesita de los jobs: consultarlos, filtrarlos y ver su historial de intentos. Solo lectura de los datos de
/// negocio; el payload (que trae datos personales del cliente) se entrega únicamente si se pide expresamente.
/// </summary>
public interface IJobAdmin
{
    /// <summary>Lista una página de jobs, del más reciente al más antiguo, que cumplan el filtro.</summary>
    Task<JobPage> ListAsync(JobFilter filter, CancellationToken cancellationToken);

    /// <summary>El detalle de un job con todos sus intentos, o <see langword="null"/> si no existe.</summary>
    /// <param name="jobId">Identificador del job.</param>
    /// <param name="includePayload">Si es verdadero, incluye el cuerpo JSON de la tienda, que contiene datos personales.</param>
    /// <param name="cancellationToken">Cancela la consulta.</param>
    Task<JobDetail?> GetAsync(Guid jobId, bool includePayload, CancellationToken cancellationToken);
}

/// <summary>Qué jobs listar. Los criterios que se dejan vacíos no filtran.</summary>
/// <param name="Statuses">Solo jobs en alguno de estos estados.</param>
/// <param name="Type">Solo jobs de este tipo (por ejemplo <c>order</c>).</param>
/// <param name="ExternalId">Solo el job de este objeto de la tienda (por ejemplo, el id del pedido).</param>
/// <param name="CreatedFrom">Solo jobs creados desde ese instante (inclusive).</param>
/// <param name="CreatedTo">Solo jobs creados antes de ese instante (exclusive).</param>
/// <param name="Page">Número de página, desde 1.</param>
/// <param name="PageSize">Jobs por página, entre 1 y <see cref="JobFilter.MaxPageSize"/>.</param>
public sealed record JobFilter(
    IReadOnlyList<JobStatus> Statuses,
    string? Type,
    string? ExternalId,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    int Page = 1,
    int PageSize = JobFilter.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Devuelve el motivo por el que el filtro no es válido, o <see langword="null"/> si lo es.</summary>
    public string? Validate() =>
        Page < 1 ? "page debe ser 1 o mayor."
        : PageSize is < 1 or > MaxPageSize ? $"pageSize debe estar entre 1 y {MaxPageSize}."
        : CreatedFrom is { } from && CreatedTo is { } to && from >= to ? "from debe ser anterior a to."
        : null;
}

/// <summary>Un job, sin su payload ni sus intentos.</summary>
public sealed record JobSummary(
    Guid Id,
    string Source,
    string ExternalId,
    string Type,
    JobStatus Status,
    int AttemptCount,
    int CycleAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    int? SapDocEntry,
    int? SapDocNum,
    string? SapCardCode,
    decimal? StoreTotal,
    decimal? SapTotal);

/// <summary>Una página de resultados.</summary>
public sealed record JobPage(IReadOnlyList<JobSummary> Items, int Page, int PageSize, int TotalItems)
{
    public int TotalPages => TotalItems == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
}

/// <summary>Un intento de procesar un job.</summary>
public sealed record JobAttemptInfo(
    int Number,
    string WorkerId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    AttemptOutcome Outcome,
    string? ErrorCode,
    string? ErrorMessage,
    ErrorKind? ErrorKind);

/// <summary>El detalle de un job.</summary>
/// <param name="Job">El job.</param>
/// <param name="Attempts">Sus intentos, del primero al último.</param>
/// <param name="Payload">El cuerpo JSON de la tienda; solo si se pidió. Contiene datos personales.</param>
public sealed record JobDetail(JobSummary Job, IReadOnlyList<JobAttemptInfo> Attempts, string? Payload);
