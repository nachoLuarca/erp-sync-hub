using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Application.Store;

/// <summary>
/// Lo que el hub necesita de la tienda. Es un puerto: Application no sabe cómo se habla con WooCommerce, y los errores
/// de la tienda llegan ya traducidos a <see cref="Result"/>, clasificados como definitivos (<see cref="ErrorKind.Rejected"/>)
/// o pasajeros (<see cref="ErrorKind.Transient"/>).
/// </summary>
public interface IStoreGateway
{
    /// <summary>
    /// Falso si la tienda no está configurada (no se definió su dirección ni sus credenciales): no hay con qué
    /// sincronizar y las demás operaciones fallarán.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Busca los productos por SKU. El resultado solo contiene los SKU que existen en la tienda; la comparación no distingue
    /// mayúsculas, pero las claves son los SKU pedidos.
    /// </summary>
    Task<Result<IReadOnlyDictionary<string, StoreProduct>>> FindProductsBySkuAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Actualiza stock y precio de varios productos. La respuesta trae un resultado por producto: que uno falle (o que falle
    /// un lote entero) no impide que los demás se actualicen. Un error general (<see cref="Result"/> fallido) se reserva
    /// para lo que afecta a toda la operación, como unas credenciales rechazadas.
    /// </summary>
    Task<Result<IReadOnlyList<StoreUpdateOutcome>>> UpdateProductsAsync(
        IReadOnlyCollection<StoreProductUpdate> updates,
        CancellationToken cancellationToken);
}

/// <summary>Producto existente en la tienda.</summary>
/// <param name="Id">Identificador del producto en la tienda.</param>
/// <param name="Sku">SKU tal como está guardado en la tienda.</param>
public sealed record StoreProduct(long Id, string Sku);

/// <summary>Cambios que se envían a un producto. Lo que es <see langword="null"/> no se toca.</summary>
/// <param name="ProductId">Producto de la tienda.</param>
/// <param name="Sku">SKU del producto (para identificarlo en los resultados y en los logs).</param>
/// <param name="StockQuantity">Nuevo stock disponible.</param>
/// <param name="Price">Nuevo precio regular, en la moneda de la tienda.</param>
public sealed record StoreProductUpdate(long ProductId, string Sku, int? StockQuantity, decimal? Price);

/// <summary>Resultado de actualizar un producto.</summary>
/// <param name="Sku">SKU del producto.</param>
/// <param name="Error">Por qué falló; <see langword="null"/> si la tienda lo actualizó.</param>
public sealed record StoreUpdateOutcome(string Sku, ResultError? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Códigos de los errores que produce la integración con la tienda.</summary>
public static class StoreErrors
{
    /// <summary>La tienda rechazó la operación; reintentarla no cambiará el resultado.</summary>
    public const string Rejected = "store.rejected";

    /// <summary>La tienda no aceptó las credenciales de la API.</summary>
    public const string Unauthorized = "store.unauthorized";

    /// <summary>La tienda respondió con un error del servidor o pidió esperar.</summary>
    public const string ServerError = "store.server_error";

    /// <summary>No se pudo conectar con la tienda.</summary>
    public const string Unreachable = "store.unreachable";

    /// <summary>La tienda no respondió a tiempo.</summary>
    public const string Timeout = "store.timeout";

    /// <summary>La tienda respondió algo que el hub no entiende.</summary>
    public const string InvalidResponse = "store.invalid_response";
}
