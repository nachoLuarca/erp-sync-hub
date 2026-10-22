using Microsoft.Extensions.Options;

namespace SapB1.Client.Internal;

internal sealed class SapB1Client : ISapB1Client
{
    private readonly SapB1Transport _transport;

    public SapB1Client(HttpClient http, SapB1SessionStore store, IOptions<SapB1Options> options)
    {
        _transport = new SapB1Transport(http, store, options.Value.PageSize);
        Items = new ItemsApi(_transport);
        BusinessPartners = new BusinessPartnersApi(_transport);
        Orders = new OrdersApi(_transport);
    }

    public IItemsApi Items { get; }

    public IBusinessPartnersApi BusinessPartners { get; }

    public IOrdersApi Orders { get; }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _transport.LogoutAsync(cancellationToken);
}
