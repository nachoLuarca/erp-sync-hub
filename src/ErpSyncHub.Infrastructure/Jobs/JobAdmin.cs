using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Jobs;

/// <summary>Consultas del panel sobre <c>sync_jobs</c> y <c>sync_attempts</c>. Todo es de solo lectura y sin seguimiento de cambios.</summary>
internal sealed class JobAdmin(ErpSyncHubDbContext context) : IJobAdmin
{
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
            .Select(job => new JobSummary(
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
                job.SapTotal))
            .ToListAsync(cancellationToken);

        return new JobPage(items, filter.Page, filter.PageSize, total);
    }

    public async Task<JobDetail?> GetAsync(Guid jobId, bool includePayload, CancellationToken cancellationToken)
    {
        var summary = await context.SyncJobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => new JobSummary(
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
                job.SapTotal))
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
}
