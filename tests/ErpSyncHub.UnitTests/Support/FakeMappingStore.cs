using ErpSyncHub.Application.Sync;

namespace ErpSyncHub.UnitTests.Support;

/// <summary>Mapeos en memoria, con el mismo comportamiento idempotente que el almacén real.</summary>
internal sealed class FakeMappingStore : IMappingStore
{
    public Dictionary<string, string> Products { get; } = [];

    public Dictionary<string, string> Customers { get; } = [];

    public Task<string?> FindItemCodeAsync(string sku, CancellationToken cancellationToken) =>
        Task.FromResult(Products.GetValueOrDefault(sku));

    public Task<string?> FindCardCodeAsync(string customerKey, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.GetValueOrDefault(customerKey));

    public Task SaveProductMappingAsync(string sku, string itemCode, CancellationToken cancellationToken)
    {
        Products.TryAdd(sku, itemCode);
        return Task.CompletedTask;
    }

    public Task SaveCustomerMappingAsync(string customerKey, string cardCode, CancellationToken cancellationToken)
    {
        Customers.TryAdd(customerKey, cardCode);
        return Task.CompletedTask;
    }
}
