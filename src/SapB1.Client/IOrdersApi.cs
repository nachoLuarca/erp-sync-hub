using SapB1.Client.Models;

namespace SapB1.Client;

/// <summary>Órdenes de venta (<c>Orders</c>) del Service Layer.</summary>
public interface IOrdersApi
{
    /// <summary>Obtiene una orden por su <c>DocEntry</c>, o <see langword="null"/> si no existe.</summary>
    Task<Order?> GetAsync(int docEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista órdenes. Para detectar si un pedido ya fue enviado, filtre por <c>NumAtCard</c>
    /// con <see cref="SapFilter.Equal(string, string)"/>.
    /// </summary>
    IAsyncEnumerable<Order> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Crea una orden de venta y devuelve el documento creado, con su <c>DocEntry</c> y <c>DocNum</c>.</summary>
    /// <exception cref="SapB1ApiException">
    /// SAP rechazó la orden: cliente o artículo inexistente, cantidad inválida, textos demasiado largos, etc.
    /// Si una línea es inválida no se crea nada.
    /// </exception>
    Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);
}
