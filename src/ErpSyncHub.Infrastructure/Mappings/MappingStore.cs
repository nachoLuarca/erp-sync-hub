using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Mappings;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Mappings;

/// <summary>
/// Mapeos SKU ↔ <c>ItemCode</c> y cliente ↔ <c>CardCode</c> en PostgreSQL. Guardar usa <c>ON CONFLICT DO NOTHING</c>: dos
/// workers que descubran el mismo mapeo a la vez no chocan, y un mapeo existente nunca se pisa.
/// </summary>
internal sealed class MappingStore(ErpSyncHubDbContext context, TimeProvider time) : IMappingStore
{
    public async Task<string?> FindItemCodeAsync(string sku, CancellationToken cancellationToken)
    {
        var key = sku.Trim();
        return await context.ProductMappings
            .AsNoTracking()
            .Where(mapping => mapping.Sku == key)
            .Select(mapping => mapping.ItemCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FindSkusAsync(
        IReadOnlyCollection<string> itemCodes,
        CancellationToken cancellationToken)
    {
        if (itemCodes.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        var codes = itemCodes.Select(code => code.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await context.ProductMappings
            .AsNoTracking()
            .Where(mapping => codes.Contains(mapping.ItemCode))
            .OrderBy(mapping => mapping.Sku)
            .Select(mapping => new { mapping.ItemCode, mapping.Sku })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ItemCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(row => row.Sku)], StringComparer.Ordinal);
    }

    public async Task<string?> FindCardCodeAsync(string customerKey, CancellationToken cancellationToken)
    {
        var key = customerKey.Trim();
        return await context.CustomerMappings
            .AsNoTracking()
            .Where(mapping => mapping.ExternalKey == key)
            .Select(mapping => mapping.CardCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveProductMappingAsync(string sku, string itemCode, CancellationToken cancellationToken)
    {
        var mapping = ProductMapping.Create(sku, itemCode, time.GetUtcNow());
        if (mapping.IsFailure)
        {
            throw new ArgumentException(mapping.Error!.Message, nameof(sku));
        }

        var value = mapping.Value;
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO product_mappings (id, sku, item_code, created_at)
            VALUES ({value.Id}, {value.Sku}, {value.ItemCode}, {value.CreatedAt})
            ON CONFLICT (sku) DO NOTHING
            """,
            cancellationToken);
    }

    public async Task SaveCustomerMappingAsync(string customerKey, string cardCode, CancellationToken cancellationToken)
    {
        var mapping = CustomerMapping.Create(customerKey, cardCode, time.GetUtcNow());
        if (mapping.IsFailure)
        {
            throw new ArgumentException(mapping.Error!.Message, nameof(customerKey));
        }

        var value = mapping.Value;
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO customer_mappings (id, external_key, card_code, created_at)
            VALUES ({value.Id}, {value.ExternalKey}, {value.CardCode}, {value.CreatedAt})
            ON CONFLICT (external_key) DO NOTHING
            """,
            cancellationToken);
    }
}
