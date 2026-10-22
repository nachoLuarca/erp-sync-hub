using SapB1.Client.Models;

namespace SapB1.Client.Internal;

internal sealed class OrdersApi(SapB1Transport transport) : IOrdersApi
{
    private const string EntitySet = "Orders";

    public Task<Order?> GetAsync(int docEntry, CancellationToken cancellationToken = default) =>
        transport.GetAsync<Order>(SapKey.Number(EntitySet, docEntry), cancellationToken);

    public IAsyncEnumerable<Order> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default) =>
        transport.QueryAsync<Order>(EntitySet, query, cancellationToken);

    public Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return transport.PostAsync<CreateOrderRequest, Order>(EntitySet, request, cancellationToken);
    }
}
