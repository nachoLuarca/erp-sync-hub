using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Jobs;

/// <summary>
/// Cola de jobs sobre la tabla <c>sync_jobs</c> de PostgreSQL (ADR 0003). El reparto entre workers usa
/// <c>FOR UPDATE SKIP LOCKED</c>: cada worker salta las filas que otro está tomando en ese instante, así no hay
/// esperas ni dos workers con el mismo job.
/// </summary>
internal sealed class PostgresJobQueue(ErpSyncHubDbContext context, TimeProvider time) : IJobQueue
{
    public async Task<EnqueueResult> EnqueueAsync(SyncJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Si entre el INSERT y la consulta alguien borra el job existente, se vuelve a intentar el INSERT.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var status = SnakeCaseEnumConverter<JobStatus>.ToDatabase(job.Status);

            // INSERT ... ON CONFLICT DO NOTHING: dos webhooks simultáneos del mismo pedido producen una sola fila,
            // sin excepciones ni condiciones de carrera.
            var inserted = await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, store_total, created_at, updated_at)
                VALUES ({job.Id}, {job.Source}, {job.ExternalId}, {job.Type}, {job.Payload}::jsonb, {status}, {job.AttemptCount}, {job.StoreTotal}, {job.CreatedAt}, {job.UpdatedAt})
                ON CONFLICT (source, external_id, type) DO NOTHING
                """,
                cancellationToken);

            if (inserted == 1)
            {
                return new EnqueueResult(job.Id, Created: true);
            }

            var existingId = await context.SyncJobs
                .AsNoTracking()
                .Where(existing => existing.Source == job.Source
                    && existing.ExternalId == job.ExternalId
                    && existing.Type == job.Type)
                .Select(existing => (Guid?)existing.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (existingId is { } id)
            {
                return new EnqueueResult(id, Created: false);
            }
        }

        throw new InvalidOperationException($"No se pudo guardar ni encontrar el job {job.Source}/{job.ExternalId}/{job.Type}.");
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId,
        int maxJobs,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxJobs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);

        var now = time.GetUtcNow();
        var pending = SnakeCaseEnumConverter<JobStatus>.ToDatabase(JobStatus.Pending);
        var processing = SnakeCaseEnumConverter<JobStatus>.ToDatabase(JobStatus.Processing);
        var retrying = SnakeCaseEnumConverter<JobStatus>.ToDatabase(JobStatus.Retrying);

        // Transacción corta: solo reparte el trabajo. El procesamiento (llamadas a SAP) ocurre fuera de ella.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // "xmin" no está incluido en "*" y es el token de concurrencia del job.
        var jobs = await context.SyncJobs
            .FromSql(
                $"""
                SELECT *, xmin FROM sync_jobs
                WHERE status = {pending}
                   OR (status = {retrying} AND next_attempt_at <= {now})
                   OR (status = {processing} AND locked_until <= {now})
                ORDER BY created_at, id
                LIMIT {maxJobs}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return [];
        }

        // Los jobs con lease vencido traen un intento "en progreso" del worker que desapareció.
        var jobIds = jobs.Select(job => job.Id).ToList();
        var orphanedAttempts = await context.SyncAttempts
            .Where(attempt => jobIds.Contains(attempt.JobId) && attempt.Outcome == AttemptOutcome.InProgress)
            .ToListAsync(cancellationToken);

        foreach (var orphan in orphanedAttempts)
        {
            orphan.Abandon(now);
        }

        var claimed = new List<ClaimedJob>(jobs.Count);
        foreach (var job in jobs)
        {
            var attempt = job.Claim(workerId, now, lease);
            if (attempt.IsFailure)
            {
                throw new InvalidOperationException($"No se pudo tomar el job {job.Id}: {attempt.Error!.Message}");
            }

            context.SyncAttempts.Add(attempt.Value);
            claimed.Add(new ClaimedJob(
                job.Id,
                attempt.Value.Id,
                attempt.Value.Number,
                workerId,
                job.Source,
                job.ExternalId,
                job.Type,
                job.Payload,
                job.StoreTotal));
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public Task<Result> CompleteAsync(ClaimedJob claim, SapOrderReference order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return CloseAsync(
            claim,
            (job, attempt, now) =>
            {
                var closed = job.MarkSucceeded(claim.WorkerId, order, now);
                return closed.IsFailure ? closed : attempt.Succeed(now);
            },
            cancellationToken);
    }

    public Task<Result> FailAsync(ClaimedJob claim, ResultError failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return CloseAsync(
            claim,
            (job, attempt, now) =>
            {
                var closed = job.MarkFailed(claim.WorkerId, failure, now);
                return closed.IsFailure ? closed : attempt.Fail(failure, now);
            },
            cancellationToken);
    }

    public Task<Result> ReleaseAsync(ClaimedJob claim, CancellationToken cancellationToken) =>
        CloseAsync(
            claim,
            (job, attempt, now) =>
            {
                var released = job.Release(claim.WorkerId, now);
                return released.IsFailure ? released : attempt.Abandon(now);
            },
            cancellationToken);

    private async Task<Result> CloseAsync(
        ClaimedJob claim,
        Func<SyncJob, SyncAttempt, DateTimeOffset, Result> close,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var job = await context.SyncJobs.SingleOrDefaultAsync(candidate => candidate.Id == claim.JobId, cancellationToken);
        var attempt = await context.SyncAttempts.SingleOrDefaultAsync(candidate => candidate.Id == claim.AttemptId, cancellationToken);

        if (job is null || attempt is null)
        {
            return Result.Failure(ResultError.NotFound("job.not_found", $"El job {claim.JobId} no existe."));
        }

        var result = close(job, attempt, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otro worker retomó el job entre la lectura y la escritura.
            return Result.Failure(ResultError.Conflict("job.lost_lease", $"El job {claim.JobId} lo tiene tomado otro worker."));
        }

        return Result.Success();
    }
}
