using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Domain.Jobs;

/// <summary>Un intento de procesar un <see cref="SyncJob"/>. Queda como historial aunque el job termine bien.</summary>
public sealed class SyncAttempt
{
    // Para EF Core.
    private SyncAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>Número de intento del job, desde 1.</summary>
    public int Number { get; private set; }

    public string WorkerId { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public AttemptOutcome Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Clasificación del error: permite a la Fase 4 decidir si reintentar.</summary>
    public ErrorKind? ErrorKind { get; private set; }

    internal static SyncAttempt Start(Guid jobId, int number, string workerId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        JobId = jobId,
        Number = number,
        WorkerId = workerId,
        StartedAt = now,
        Outcome = AttemptOutcome.InProgress,
    };

    public Result Succeed(DateTimeOffset now) => Close(AttemptOutcome.Succeeded, null, now);

    public Result Fail(ResultError error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Close(AttemptOutcome.Failed, error, now);
    }

    public Result Abandon(DateTimeOffset now) => Close(AttemptOutcome.Abandoned, null, now);

    private Result Close(AttemptOutcome outcome, ResultError? error, DateTimeOffset now)
    {
        if (Outcome != AttemptOutcome.InProgress)
        {
            return Result.Failure(
                ResultError.Conflict("attempt.already_closed", $"El intento {Id} ya terminó: {Outcome}."));
        }

        Outcome = outcome;
        FinishedAt = now;

        if (error is not null)
        {
            ErrorCode = SyncJob.Truncate(error.Code, SyncJob.MaxErrorCodeLength);
            ErrorMessage = SyncJob.Truncate(error.Message, SyncJob.MaxErrorMessageLength);
            ErrorKind = error.Kind;
        }

        return Result.Success();
    }
}
