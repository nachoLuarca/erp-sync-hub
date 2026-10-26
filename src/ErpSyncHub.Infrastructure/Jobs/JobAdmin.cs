using System.Linq.Expressions;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Infrastructure.Jobs;

/// <summary>Consultas y reintentos manuales del panel sobre <c>sync_jobs</c> y <c>sync_attempts</c>.</summary>
internal sealed partial class JobAdmin(ErpSyncHubDbContext context, TimeProvider time, ILogger<JobAdmin> logger) : IJobAdmin
{
    // La misma proyección sirve para consultar en la base de datos y para convertir un job que ya está en memoria.
    private static readonly Expression<Func<SyncJob, JobSummary>> ToSummary = job => new JobSummary(
        job.Id,
        job.Source,
        job.ExternalId,
        job.Type,
        job.Status,
        job.AttemptCount,
        job.CycleAttempts,
        job.NextAttemptAt,
        job.LastErrorCode,
        job.LastErrorMessage,
        job.CreatedAt,
        job.UpdatedAt,
        job.FinishedAt,
        job.SapDocEntry,
        job.SapDocNum,
        job.SapCardCode,
        job.StoreTotal,
        job.SapTotal);

    private static readonly Func<SyncJob, JobSummary> Summarize = ToSummary.Compile();

    public async Task<JobPage> ListAsync(JobFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(filter));
        }

        var query = context.SyncJobs.AsNoTracking();

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.Distinct().ToArray();
            query = query.Where(job => statuses.Contains(job.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            query = query.Where(job => job.Type == filter.Type);
        }

        if (!string.IsNullOrWhiteSpace(filter.ExternalId))
        {
            query = query.Where(job => job.ExternalId == filter.ExternalId);
        }

        if (filter.CreatedFrom is { } from)
        {
            query = query.Where(job => job.CreatedAt >= from);
        }

        if (filter.CreatedTo is { } to)
        {
            query = query.Where(job => job.CreatedAt < to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(ToSummary)
            .ToListAsync(cancellationToken);

        return new JobPage(items, filter.Page, filter.PageSize, total);
    }

    public async Task<JobDetail?> GetAsync(Guid jobId, bool includePayload, CancellationToken cancellationToken)
    {
        var summary = await context.SyncJobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(ToSummary)
            .SingleOrDefaultAsync(cancellationToken);

        if (summary is null)
        {
            return null;
        }

        var attempts = await context.SyncAttempts
            .AsNoTracking()
            .Where(attempt => attempt.JobId == jobId)
            .OrderBy(attempt => attempt.Number)
            .Select(attempt => new JobAttemptInfo(
                attempt.Number,
                attempt.WorkerId,
                attempt.StartedAt,
                attempt.FinishedAt,
                attempt.Outcome,
                attempt.ErrorCode,
                attempt.ErrorMessage,
                attempt.ErrorKind))
            .ToListAsync(cancellationToken);

        // El payload se lee aparte para no traer datos personales del disco cuando nadie los pidió.
        var payload = includePayload
            ? await context.SyncJobs.AsNoTracking().Where(job => job.Id == jobId).Select(job => job.Payload).SingleAsync(cancellationToken)
            : null;

        return new JobDetail(summary, attempts, payload);
    }

    public async Task<IReadOnlyList<JobRetryOutcome>> RetryAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        var ids = jobIds.Distinct().ToList();
        var outcomes = new List<JobRetryOutcome>(ids.Count);

        foreach (var id in ids)
        {
            outcomes.Add(await RetryOneAsync(id, cancellationToken));
        }

        var retried = outcomes.Count(outcome => outcome.Result == JobRetryResult.Retried);
        LogRetried(retried, ids.Count);
        return outcomes;
    }

    private async Task<JobRetryOutcome> RetryOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await context.SyncJobs.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (job is null)
        {
            return new JobRetryOutcome(id, JobRetryResult.NotFound, null, $"No existe el job {id}.");
        }

        var retried = job.RetryNow(time.GetUtcNow());
        if (retried.IsFailure)
        {
            return new JobRetryOutcome(id, JobRetryResult.NotRetryable, Summarize(job), retried.Error!.Message);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Un worker tomó el job entre la lectura y la escritura; el seguimiento de cambios se limpia para no arrastrar el estado a medias.
            context.ChangeTracker.Clear();
            var current = await context.SyncJobs.AsNoTracking().Where(candidate => candidate.Id == id).Select(ToSummary).SingleOrDefaultAsync(cancellationToken);
            return new JobRetryOutcome(id, JobRetryResult.Conflict, current, $"Un worker tomó el job {id} mientras se reintentaba: vuelve a intentarlo.");
        }

        return new JobRetryOutcome(id, JobRetryResult.Retried, Summarize(job), null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reintento manual desde el panel: {Retried} de {Requested} jobs quedaron disponibles")]
    private partial void LogRetried(int retried, int requested);
}
