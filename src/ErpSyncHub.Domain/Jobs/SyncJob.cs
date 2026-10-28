using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Domain.Jobs;

/// <summary>
/// Trabajo de sincronización recibido de la tienda. Funciona como bandeja de entrada (inbox) y como cola:
/// se guarda al recibir el webhook y un worker lo toma después. El trío origen + id externo + tipo es único,
/// lo que hace idempotente la recepción.
/// </summary>
public sealed class SyncJob
{
    public const int MaxSourceLength = 50;
    public const int MaxExternalIdLength = 100;
    public const int MaxTypeLength = 50;
    public const int MaxWorkerIdLength = 100;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorMessageLength = 2000;

    // Para EF Core.
    private SyncJob()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Sistema de origen, por ejemplo <see cref="SyncSources.WooCommerce"/>.</summary>
    public string Source { get; private set; } = string.Empty;

    /// <summary>Identificador del objeto en el sistema de origen (el id del pedido en la tienda).</summary>
    public string ExternalId { get; private set; } = string.Empty;

    /// <summary>Qué se sincroniza, por ejemplo <see cref="SyncJobTypes.Order"/>.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>Cuerpo JSON tal como lo envió la tienda.</summary>
    public string Payload { get; private set; } = string.Empty;

    public JobStatus Status { get; private set; }

    /// <summary>Cantidad de veces que un worker tomó el job, en toda su vida.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// Intentos del ciclo actual: cuenta contra el máximo de reintentos automáticos y vuelve a cero con un reintento manual.
    /// Incluye los intentos abandonados (un worker que cayó), para que un job que tumba al worker no se reintente sin fin.
    /// </summary>
    public int CycleAttempts { get; private set; }

    /// <summary>Cuándo puede tomarse otra vez un job en <see cref="JobStatus.Retrying"/>.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Total del pedido según la tienda (con envío e impuestos).</summary>
    public decimal? StoreTotal { get; private set; }

    /// <summary>Total de la orden creada en SAP (suma de sus líneas, sin envío ni impuestos).</summary>
    public decimal? SapTotal { get; private set; }

    public int? SapDocEntry { get; private set; }

    public int? SapDocNum { get; private set; }

    /// <summary>Cliente de SAP al que quedó asociada la orden.</summary>
    public string? SapCardCode { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    /// <summary>Worker que tiene el job tomado.</summary>
    public string? LockedBy { get; private set; }

    /// <summary>Hasta cuándo vale el lease del worker; pasada esa hora otro worker puede retomar el job.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cuándo terminó, con éxito o con fallo.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Indica si el SAP total difiere del total de la tienda.</summary>
    public bool HasTotalMismatch =>
        StoreTotal is { } store && SapTotal is { } sap && Math.Abs(store - sap) > TotalTolerance;

    /// <summary>Diferencia máxima que se considera redondeo y no una discrepancia.</summary>
    public static decimal TotalTolerance => 0.01m;

    public static Result<SyncJob> Create(
        string source,
        string externalId,
        string type,
        string payload,
        decimal? storeTotal,
        DateTimeOffset now)
    {
        var error = Require(source, MaxSourceLength, nameof(source))
            ?? Require(externalId, MaxExternalIdLength, nameof(externalId))
            ?? Require(type, MaxTypeLength, nameof(type))
            ?? Require(payload, int.MaxValue, nameof(payload));

        if (error is not null)
        {
            return Result.Failure<SyncJob>(error);
        }

        var job = new SyncJob
        {
            Id = Guid.CreateVersion7(now),
            Source = source,
            ExternalId = externalId,
            Type = type,
            Payload = payload,
            Status = JobStatus.Pending,
            StoreTotal = storeTotal,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return Result.Success(job);
    }

    /// <summary>
    /// Un worker puede tomarlo si está pendiente, si espera un reintento cuya hora ya llegó, o si estaba tomado pero su
    /// lease venció.
    /// </summary>
    public bool IsClaimable(DateTimeOffset now) =>
        Status == JobStatus.Pending
        || (Status == JobStatus.Retrying && NextAttemptAt <= now)
        || (Status == JobStatus.Processing && LockedUntil <= now);

    /// <summary>Lo toma un worker por el tiempo del lease y registra un nuevo intento.</summary>
    public Result<SyncAttempt> Claim(string workerId, DateTimeOffset now, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > MaxWorkerIdLength)
        {
            return Result.Failure<SyncAttempt>(
                ResultError.Validation("job.invalid_worker", $"El worker debe tener entre 1 y {MaxWorkerIdLength} caracteres."));
        }

        if (lease <= TimeSpan.Zero)
        {
            return Result.Failure<SyncAttempt>(
                ResultError.Validation("job.invalid_lease", "El lease debe ser mayor que cero."));
        }

        if (!IsClaimable(now))
        {
            return Result.Failure<SyncAttempt>(
                ResultError.Conflict("job.not_claimable", $"El job {Id} no se puede tomar: está {Status}."));
        }

        AttemptCount++;
        CycleAttempts++;
        NextAttemptAt = null;
        Status = JobStatus.Processing;
        LockedBy = workerId;
        LockedUntil = now + lease;
        UpdatedAt = now;

        return Result.Success(SyncAttempt.Start(Id, AttemptCount, workerId, now));
    }

    /// <summary>Cierra el job con éxito, guardando la orden creada en SAP.</summary>
    public Result MarkSucceeded(string workerId, SapOrderReference order, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);

        var ownership = EnsureOwnedBy(workerId);
        if (ownership is not null)
        {
            return Result.Failure(ownership);
        }

        Status = JobStatus.Succeeded;
        LastErrorCode = null;
        LastErrorMessage = null;
        SapDocEntry = order.DocEntry;
        SapDocNum = order.DocNum;
        SapCardCode = order.CardCode;
        SapTotal = order.Total;
        Finish(now);
        return Result.Success();
    }

    /// <summary>
    /// Devuelve el job a la cola sin cerrarlo: lo usa un worker que se apaga, para que otro lo tome de inmediato en lugar
    /// de esperar a que venza el lease. El intento queda registrado y el contador no retrocede.
    /// </summary>
    public Result Release(string workerId, DateTimeOffset now)
    {
        var ownership = EnsureOwnedBy(workerId);
        if (ownership is not null)
        {
            return Result.Failure(ownership);
        }

        Status = JobStatus.Pending;
        LockedBy = null;
        LockedUntil = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Devuelve el job a la cola porque el circuit breaker de SAP está abierto: el intento no llegó a SAP, así que no cuenta. El
    /// presupuesto de intentos del ciclo vuelve a donde estaba (el historial de intentos, no: el intento queda registrado como
    /// abandonado).
    /// </summary>
    public Result Defer(string workerId, DateTimeOffset now)
    {
        var ownership = EnsureOwnedBy(workerId);
        if (ownership is not null)
        {
            return Result.Failure(ownership);
        }

        Status = JobStatus.Pending;
        CycleAttempts = Math.Max(0, CycleAttempts - 1);
        LockedBy = null;
        LockedUntil = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Cierra el job como fallido por un error definitivo, guardando el motivo. Reintentar no cambiaría nada: queda así hasta
    /// que una persona lo corrija y lo reintente a mano.
    /// </summary>
    public Result MarkFailed(string workerId, ResultError error, DateTimeOffset now) =>
        CloseWithError(workerId, error, JobStatus.Failed, now);

    /// <summary>
    /// Cierra el job en el dead letter: el error era transitorio pero se agotaron los intentos automáticos. Queda a la vista
    /// en el panel hasta que una persona lo reintente.
    /// </summary>
    public Result MarkDeadLetter(string workerId, ResultError error, DateTimeOffset now) =>
        CloseWithError(workerId, error, JobStatus.DeadLetter, now);

    /// <summary>
    /// Deja el job esperando para reintentarlo a la hora indicada tras un error transitorio. Conserva el motivo del último
    /// fallo y libera el job: cualquier worker podrá tomarlo cuando llegue la hora.
    /// </summary>
    public Result ScheduleRetry(string workerId, ResultError error, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);

        var ownership = EnsureOwnedBy(workerId);
        if (ownership is not null)
        {
            return Result.Failure(ownership);
        }

        Status = JobStatus.Retrying;
        NextAttemptAt = nextAttemptAt;
        SaveError(error);
        LockedBy = null;
        LockedUntil = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Reintento manual: deja el job disponible de inmediato con un presupuesto de intentos nuevo. Sirve para un job fallido o
    /// en el dead letter, y para saltarse la espera de uno que aguarda su reintento. Conserva el último error y el historial
    /// de intentos. No se puede reintentar un job en proceso ni uno que ya terminó con éxito.
    /// </summary>
    public Result RetryNow(DateTimeOffset now)
    {
        if (Status is JobStatus.Processing or JobStatus.Succeeded)
        {
            return Result.Failure(ResultError.Conflict(
                "job.not_retryable",
                Status == JobStatus.Processing
                    ? $"El job {Id} está en proceso: espera a que termine el intento."
                    : $"El job {Id} ya terminó con éxito: no hay nada que reintentar."));
        }

        Status = JobStatus.Pending;
        NextAttemptAt = null;
        CycleAttempts = 0;
        FinishedAt = null;
        LockedBy = null;
        LockedUntil = null;
        UpdatedAt = now;
        return Result.Success();
    }

    internal static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static ResultError? Require(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ResultError.Validation("job.invalid_field", $"'{name}' es obligatorio.");
        }

        return value.Length > maxLength
            ? ResultError.Validation("job.invalid_field", $"'{name}' excede {maxLength} caracteres.")
            : null;
    }

    /// <summary>Solo el worker que tiene el job puede cerrarlo; si el lease venció y otro lo retomó, ya no es suyo.</summary>
    private ResultError? EnsureOwnedBy(string workerId)
    {
        if (Status != JobStatus.Processing)
        {
            return ResultError.Conflict("job.not_processing", $"El job {Id} no está en proceso: está {Status}.");
        }

        return LockedBy == workerId
            ? null
            : ResultError.Conflict("job.lost_lease", $"El job {Id} lo tiene tomado otro worker.");
    }

    private Result CloseWithError(string workerId, ResultError error, JobStatus terminal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);

        var ownership = EnsureOwnedBy(workerId);
        if (ownership is not null)
        {
            return Result.Failure(ownership);
        }

        Status = terminal;
        NextAttemptAt = null;
        SaveError(error);
        Finish(now);
        return Result.Success();
    }

    private void SaveError(ResultError error)
    {
        LastErrorCode = Truncate(error.Code, MaxErrorCodeLength);
        LastErrorMessage = Truncate(error.Message, MaxErrorMessageLength);
    }

    private void Finish(DateTimeOffset now)
    {
        LockedBy = null;
        LockedUntil = null;
        UpdatedAt = now;
        FinishedAt = now;
    }
}

/// <summary>La orden de venta creada en SAP para un pedido.</summary>
/// <param name="DocEntry">Clave interna del documento.</param>
/// <param name="DocNum">Número visible del documento.</param>
/// <param name="CardCode">Cliente de SAP de la orden.</param>
/// <param name="Total">Total del documento en SAP.</param>
public sealed record SapOrderReference(int DocEntry, int DocNum, string CardCode, decimal Total);
