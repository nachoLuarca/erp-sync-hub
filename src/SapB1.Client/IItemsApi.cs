using SapB1.Client.Models;

namespace SapB1.Client;

/// <summary>Artículos (<c>Items</c>) del Service Layer.</summary>
public interface IItemsApi
{
    /// <summary>Obtiene un artículo por su código, o <see langword="null"/> si no existe.</summary>
    Task<Item?> GetAsync(string itemCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista artículos. Recorre las páginas del servidor a medida que se enumera, por lo que
    /// puede cancelarse sin haber descargado toda la colección.
    /// </summary>
    IAsyncEnumerable<Item> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default);
}
