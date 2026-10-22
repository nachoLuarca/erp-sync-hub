using SapB1.Mock.Admin;

namespace SapB1.Mock.Orders;

internal sealed class InMemoryOrderStore : IOrderStore, IResettable
{
    /// <summary>Primer <c>DocNum</c> de la serie; distinto de <c>DocEntry</c> para detectar clientes que los confundan.</summary>
    public const int FirstDocNum = 1001;

    private readonly Lock _gate = new();
    private readonly List<Order> _orders = [];

    public IReadOnlyList<Order> GetAll()
    {
        lock (_gate)
        {
            return [.. _orders];
        }
    }

    public Order? Find(int docEntry)
    {
        lock (_gate)
        {
            return _orders.Find(order => order.DocEntry == docEntry);
        }
    }

    public Order Add(Order order)
    {
        lock (_gate)
        {
            var stored = order with
            {
                DocEntry = _orders.Count + 1,
                DocNum = FirstDocNum + _orders.Count,
            };

            _orders.Add(stored);
            return stored;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _orders.Clear();
        }
    }
}
