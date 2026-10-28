using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Application.Jobs;

/// <summary>
/// Cola de jobs de sincronización. Recibe los trabajos que llegan de la tienda (inbox) y se los entrega a los workers
/// de a uno por vez, sin que dos workers tomen el mismo.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Guarda el job si es nuevo. Si ya existe uno con el mismo origen, id externo y tipo no hace nada y devuelve el
    /// existente: recibir dos veces el mismo pedido es normal (la tienda reintenta) y no es un error.
    /// </summary>
    Task<EnqueueResult> EnqueueAsync(SyncJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Toma hasta <paramref name="maxJobs"/> jobs, los más antiguos primero: los pendientes y los que otro worker
    /// dejó con el lease vencido. Cada job queda tomado por <paramref name="workerId"/> durante <paramref name="lease"/>
    /// y con un intento nuevo en progreso. Nunca bloquea esperando jobs que otro worker está tomando.
    /// </summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string workerId, int maxJobs, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Cierra el job con éxito. Falla con <c>job.lost_lease</c> si otro worker lo retomó mientras tanto.</summary>
    Task<Result> CompleteAsync(ClaimedJob claim, SapOrderReference order, CancellationToken cancellationToken);

    /// <summary>
    /// Registra el fallo del intento y deja el job según <paramref name="decision"/>: esperando para reintentarlo
    /// (<see cref="RetryAction.RetryLater"/>), en el dead letter (<see cref="RetryAction.DeadLetter"/>) o fallido
    /// definitivamente (<see cref="RetryAction.Fail"/>). Falla con <c>job.lost_lease</c> si otro worker lo retomó mientras tanto.
    /// </summary>
    Task<Result> FailAsync(ClaimedJob claim, ResultError failure, RetryDecision decision, CancellationToken cancellationToken);

    /// <summary>
    /// Devuelve el job a la cola sin cerrarlo y deja su intento como abandonado. Lo usa un worker que se apaga para que
    /// otro lo tome de inmediato. Falla con <c>job.lost_lease</c> si otro worker lo retomó mientras tanto.
    /// </summary>
    Task<Result> ReleaseAsync(ClaimedJob claim, CancellationToken cancellationToken);

    /// <summary>
    /// Devuelve el job a la cola porque el circuit breaker de SAP estaba abierto: el intento no llegó a SAP, así que no se descuenta
    /// del presupuesto de intentos del job. Falla con <c>job.lost_lease</c> si otro worker lo retomó mientras tanto.
    /// </summary>
    Task<Result> DeferAsync(ClaimedJob claim, CancellationToken cancellationToken);
}

/// <param name="JobId">Identificador del job, nuevo o ya existente.</param>
/// <param name="Created"><see langword="true"/> si se guardó; <see langword="false"/> si ya estaba.</param>
public sealed record EnqueueResult(Guid JobId, bool Created);

/// <summary>
/// Un job que un worker tiene tomado, con lo necesario para procesarlo y cerrarlo. <c>AttemptNumber</c> cuenta los intentos de
/// toda la vida del job; <c>CycleAttempt</c>, los del ciclo actual, que son los que cuentan contra el máximo de reintentos
/// automáticos.
/// </summary>
public sealed record ClaimedJob(
    Guid JobId,
    Guid AttemptId,
    int AttemptNumber,
    string WorkerId,
    string Source,
    string ExternalId,
    string Type,
    string Payload,
    decimal? StoreTotal,
    int CycleAttempt = 1);
