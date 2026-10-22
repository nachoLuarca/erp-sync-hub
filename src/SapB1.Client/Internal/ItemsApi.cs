using SapB1.Client.Models;

namespace SapB1.Client.Internal;

internal sealed class ItemsApi(SapB1Transport transport) : IItemsApi
{
    private const string EntitySet = "Items";

    public Task<Item?> GetAsync(string itemCode, CancellationToken cancellationToken = default) =>
        transport.GetAsync<Item>(SapKey.Text(EntitySet, itemCode), cancellationToken);

    public IAsyncEnumerable<Item> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default) =>
        transport.QueryAsync<Item>(EntitySet, query, cancellationToken);
}
