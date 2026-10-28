using ErpSyncHub.Application.Admin;
using ErpSyncHub.Domain.Admin;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Admin;

/// <summary>Auditoría del panel en la tabla <c>admin_audit</c>. Solo inserta y lee: no hay forma de cambiar lo ya registrado.</summary>
internal sealed class AdminAuditStore(ErpSyncHubDbContext context) : IAdminAudit
{
    public async Task RecordAsync(AdminAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        context.AdminAudit.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdminAuditPage> ListAsync(AdminAuditFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.PageSize, 1);

        var query = context.AdminAudit.AsNoTracking();

        if (filter.Action is not null)
        {
            query = query.Where(entry => entry.Action == filter.Action);
        }

        if (filter.KeyFingerprint is not null)
        {
            query = query.Where(entry => entry.KeyFingerprint == filter.KeyFingerprint);
        }

        if (filter.From is { } from)
        {
            query = query.Where(entry => entry.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(entry => entry.OccurredAt < to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        return new AdminAuditPage(items, filter.Page, filter.PageSize, total);
    }
}
