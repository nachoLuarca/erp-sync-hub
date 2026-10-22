namespace SapB1.Mock.Orders;

/// <summary>Persistencia de las órdenes de venta del mock.</summary>
internal interface IOrderStore
{
    IReadOnlyList<Order> GetAll();

    Order? Find(int docEntry);

    /// <summary>Guarda la orden asignándole el siguiente <c>DocEntry</c> y <c>DocNum</c>.</summary>
    Order Add(Order order);
}
