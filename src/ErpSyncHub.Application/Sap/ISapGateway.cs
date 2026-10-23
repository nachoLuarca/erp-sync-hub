using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Application.Sap;

/// <summary>
/// Lo que el hub necesita de SAP Business One. Es un puerto: Application no sabe cómo se habla con SAP, y los errores
/// de SAP llegan ya traducidos a <see cref="Result"/>, clasificados como definitivos (<see cref="ErrorKind.Rejected"/>)
/// o pasajeros (<see cref="ErrorKind.Transient"/>).
/// </summary>
public interface ISapGateway
{
    /// <summary>Busca un cliente por RUT. Devuelve éxito con <see langword="null"/> si no existe.</summary>
    Task<Result<SapCustomer?>> FindCustomerByTaxIdAsync(Rut taxId, CancellationToken cancellationToken);

    /// <summary>Busca un cliente por correo (en minúsculas). Devuelve éxito con <see langword="null"/> si no existe.</summary>
    Task<Result<SapCustomer?>> FindCustomerByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Obtiene un cliente por su código. Devuelve éxito con <see langword="null"/> si no existe.</summary>
    Task<Result<SapCustomer?>> GetCustomerAsync(string cardCode, CancellationToken cancellationToken);

    /// <summary>
    /// Crea el cliente, o devuelve el que ya existe con ese <c>CardCode</c>: crearlo dos veces (por ejemplo, al
    /// reanudar un job que se cortó a medias) no es un error.
    /// </summary>
    Task<Result<SapCustomer>> EnsureCustomerAsync(NewSapCustomer customer, CancellationToken cancellationToken);

    /// <summary>Obtiene un artículo por su código. Devuelve éxito con <see langword="null"/> si no existe.</summary>
    Task<Result<SapItem?>> GetItemAsync(string itemCode, CancellationToken cancellationToken);

    /// <summary>
    /// Busca la orden de venta que se creó para un pedido, por la referencia del cliente (<c>NumAtCard</c>).
    /// Devuelve éxito con <see langword="null"/> si no existe.
    /// </summary>
    Task<Result<SapOrderReference?>> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Crea la orden de venta.</summary>
    Task<Result<SapOrderReference>> CreateOrderAsync(NewSapOrder order, CancellationToken cancellationToken);
}

/// <summary>Cliente existente en SAP.</summary>
/// <param name="CardCode">Código del cliente.</param>
/// <param name="CardName">Nombre o razón social.</param>
/// <param name="TaxId">Identificador tributario tal como está guardado en SAP.</param>
/// <param name="Email">Correo.</param>
/// <param name="IsActive">Falso si el cliente está inhabilitado o congelado: SAP no aceptará órdenes para él.</param>
public sealed record SapCustomer(string CardCode, string CardName, string? TaxId, string? Email, bool IsActive);

/// <summary>Cliente que se va a crear en SAP.</summary>
/// <param name="CardCode">Código determinista, ver <see cref="SapCardCodes"/>.</param>
/// <param name="CardName">Nombre o razón social.</param>
/// <param name="TaxId">RUT validado, si se conoce.</param>
/// <param name="Email">Correo, en minúsculas.</param>
/// <param name="Phone">Teléfono.</param>
/// <param name="Addresses">Direcciones de facturación y de despacho.</param>
public sealed record NewSapCustomer(
    string CardCode,
    string CardName,
    Rut? TaxId,
    string? Email,
    string? Phone,
    IReadOnlyList<SapAddress> Addresses);

/// <summary>Tipo de dirección de un cliente.</summary>
public enum SapAddressKind
{
    Billing,
    Shipping,
}

/// <summary>Dirección de un cliente.</summary>
public sealed record SapAddress(string Name, SapAddressKind Kind, string? Street, string? City, string? Country, string? PostalCode);

/// <summary>Artículo existente en SAP.</summary>
/// <param name="ItemCode">Código del artículo.</param>
/// <param name="Name">Descripción.</param>
/// <param name="IsActive">Falso si el artículo está inhabilitado o congelado.</param>
public sealed record SapItem(string ItemCode, string Name, bool IsActive);

/// <summary>Orden de venta que se va a crear en SAP.</summary>
/// <param name="CardCode">Cliente de la orden.</param>
/// <param name="Reference">Referencia del cliente (<c>NumAtCard</c>): el identificador del pedido en la tienda.</param>
/// <param name="Comments">Observaciones.</param>
/// <param name="DocumentDate">Fecha del pedido; si es <see langword="null"/>, SAP usa la de hoy.</param>
/// <param name="Lines">Líneas de la orden.</param>
public sealed record NewSapOrder(
    string CardCode,
    string Reference,
    string? Comments,
    DateOnly? DocumentDate,
    IReadOnlyList<NewSapOrderLine> Lines);

/// <summary>Línea de una orden de venta nueva.</summary>
/// <param name="ItemCode">Artículo.</param>
/// <param name="Quantity">Cantidad.</param>
/// <param name="UnitPrice">Precio unitario.</param>
public sealed record NewSapOrderLine(string ItemCode, decimal Quantity, decimal UnitPrice);

/// <summary>Códigos de los errores que produce la integración con SAP.</summary>
public static class SapErrors
{
    /// <summary>SAP rechazó la operación; reintentarla no cambiará el resultado.</summary>
    public const string Rejected = "sap.rejected";

    /// <summary>SAP respondió con un error del servidor o pidió esperar.</summary>
    public const string ServerError = "sap.server_error";

    /// <summary>No se pudo iniciar sesión en SAP, o SAP siguió rechazando la sesión.</summary>
    public const string Session = "sap.session";

    /// <summary>No se pudo conectar con SAP.</summary>
    public const string Unreachable = "sap.unreachable";

    /// <summary>SAP no respondió a tiempo.</summary>
    public const string Timeout = "sap.timeout";

    /// <summary>SAP dijo que el cliente ya existe, pero luego no se pudo leer.</summary>
    public const string CustomerConflict = "sap.customer_conflict";
}
