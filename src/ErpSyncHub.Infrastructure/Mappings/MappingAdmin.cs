using ErpSyncHub.Application.Sync;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Mappings;

/// <summary>Listado de los mapeos para el panel, paginado y con filtros.</summary>
internal sealed class MappingAdmin(ErpSyncHubDbContext context) : IMappingAdmin
{
    public async Task<MappingPage<ProductMappingInfo>> ListProductsAsync(ProductMappingFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.PageSize, 1);

        var query = context.ProductMappings.AsNoTracking();

        if (filter.Sku is not null)
        {
            var pattern = Contains(filter.Sku);
            query = query.Where(mapping => EF.Functions.ILike(mapping.Sku, pattern, "\\"));
        }

        if (filter.ItemCode is not null)
        {
            var pattern = Contains(filter.ItemCode);
            query = query.Where(mapping => EF.Functions.ILike(mapping.ItemCode, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(mapping => mapping.Sku)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(mapping => new ProductMappingInfo(mapping.Sku, mapping.ItemCode, mapping.CreatedAt))
            .ToListAsync(cancellationToken);

        return new MappingPage<ProductMappingInfo>(items, filter.Page, filter.PageSize, total);
    }

    public async Task<MappingPage<CustomerMappingInfo>> ListCustomersAsync(CustomerMappingFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.PageSize, 1);

        var query = context.CustomerMappings.AsNoTracking();

        if (filter.Key is not null)
        {
            query = query.Where(mapping => mapping.ExternalKey == filter.Key);
        }

        if (filter.CardCode is not null)
        {
            query = query.Where(mapping => mapping.CardCode == filter.CardCode);
        }

        if (filter.Kind == CustomerKeyKind.Registered)
        {
            query = query.Where(mapping => mapping.ExternalKey.StartsWith("customer:"));
        }
        else if (filter.Kind == CustomerKeyKind.Guest)
        {
            query = query.Where(mapping => mapping.ExternalKey.StartsWith("email:"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(mapping => mapping.CreatedAt)
            .ThenBy(mapping => mapping.ExternalKey)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(mapping => new CustomerMappingInfo(mapping.ExternalKey, mapping.CardCode, mapping.CreatedAt))
            .ToListAsync(cancellationToken);

        return new MappingPage<CustomerMappingInfo>(items, filter.Page, filter.PageSize, total);
    }

    /// <summary>El patrón <c>%texto%</c> de ILIKE con los comodines del propio texto escapados.</summary>
    private static string Contains(string text) =>
        $"%{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
}
