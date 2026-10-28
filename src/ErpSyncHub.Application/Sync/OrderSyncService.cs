using System.Globalization;
using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Domain.Mappings;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Application.Sync;

/// <param name="Order">La orden de venta en SAP.</param>
/// <param name="AlreadyExisted"><see langword="true"/> si la orden ya estaba creada y esta pasada no creó nada.</param>
public sealed record OrderSyncOutcome(SapOrderReference Order, bool AlreadyExisted);

/// <summary>Códigos de los errores definitivos que detecta la sincronización de un pedido.</summary>
public static class OrderSyncErrors
{
    public const string InvalidPayload = "order.invalid_payload";
    public const string WithoutLines = "order.without_lines";
    public const string InvalidTaxId = "order.invalid_tax_id";
    public const string UnidentifiableCustomer = "order.customer_unidentifiable";
    public const string InactiveCustomer = "order.inactive_customer";
    public const string LineWithoutSku = "order.line_without_sku";
    public const string InvalidQuantity = "order.invalid_quantity";
    public const string UnknownSku = "order.unknown_sku";
    public const string InactiveItem = "order.inactive_item";
}

/// <summary>
/// Convierte un pedido de WooCommerce en una orden de venta de SAP (ADR 0008). Cada paso es idempotente, así que
/// reprocesar un job que se cortó a medias nunca duplica nada:
/// <list type="number">
/// <item>Busca la orden por su referencia (<c>WC-{id}</c>): si ya existe, termina sin tocar nada.</item>
/// <item>Resuelve cada producto: mapeo guardado, o el SKU como <c>ItemCode</c> si SAP lo conoce.</item>
/// <item>Resuelve el cliente: mapeo guardado, RUT validado, correo, o lo crea con un código determinista.</item>
/// <item>Crea la orden con el precio unitario igual al total de la línea (con cupones) dividido por la cantidad.</item>
/// </list>
/// El envío y los impuestos quedan fuera de la orden; si el total de SAP difiere del de la tienda, se registra una advertencia.
/// </summary>
public sealed partial class OrderSyncService(
    ISapGateway sap,
    IMappingStore mappings,
    OrderSyncOptions options,
    ILogger<OrderSyncService> logger)
{
    private const int MaxNameLength = 100;
    private const int MaxPhoneLength = 20;
    private const int MaxStreetLength = 100;
    private const int MaxPostalCodeLength = 20;
    private const int MaxCommentsLength = 254;
    private const int PriceDecimals = 4;

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);

    public async Task<Result<OrderSyncOutcome>> SyncAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var parsed = WooCommerceOrderParser.Parse(job.Payload);
        if (parsed.IsFailure)
        {
            return Rejected(OrderSyncErrors.InvalidPayload, $"El pedido guardado en el job no es válido: {parsed.Error!.Message}");
        }

        var order = parsed.Value;

        // Desde aquí, cualquier mensaje de SAP o de la tienda que repita un dato de este pedido sale con el dato oculto.
        using var personalData = PersonalDataScope.Begin(OrderPersonalData.Of(order, options.TaxIdMetaKey));

        var reference = $"{OrderSyncOptions.ReferencePrefix}{order.Id.ToString(CultureInfo.InvariantCulture)}";

        // Validaciones locales primero: no vale la pena llamar a SAP por un pedido que se sabe inválido.
        var localError = ValidateLocally(order);
        if (localError is not null)
        {
            return Result.Failure<OrderSyncOutcome>(localError);
        }

        // 1. Si la orden ya existe (un intento anterior alcanzó a crearla), no se vuelve a crear.
        var existing = await sap.FindOrderByReferenceAsync(reference, cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure<OrderSyncOutcome>(existing.Error!);
        }

        if (existing.Value is { } found)
        {
            LogOrderAlreadyExists(order.Id, found.DocEntry, found.DocNum);
            WarnIfTotalsDiffer(order, job.StoreTotal, found.Total);
            return Result.Success(new OrderSyncOutcome(found, AlreadyExisted: true));
        }

        // 2. Productos. Antes que el cliente: un SKU inválido no debe dejar un cliente nuevo en SAP para un pedido que va a fallar.
        var lines = await BuildLinesAsync(order, cancellationToken);
        if (lines.IsFailure)
        {
            return Result.Failure<OrderSyncOutcome>(lines.Error!);
        }

        // 3. Cliente.
        var customer = await ResolveCustomerAsync(order, cancellationToken);
        if (customer.IsFailure)
        {
            return Result.Failure<OrderSyncOutcome>(customer.Error!);
        }

        // 4. Orden.
        var created = await sap.CreateOrderAsync(
            new NewSapOrder(customer.Value, reference, BuildComments(order), ToStoreDate(order), lines.Value),
            cancellationToken);
        if (created.IsFailure)
        {
            return Result.Failure<OrderSyncOutcome>(created.Error!);
        }

        LogOrderCreated(order.Id, created.Value.DocEntry, created.Value.DocNum);
        WarnIfTotalsDiffer(order, job.StoreTotal, created.Value.Total);
        return Result.Success(new OrderSyncOutcome(created.Value, AlreadyExisted: false));
    }

    private static ResultError? ValidateLocally(WooCommerceOrder order)
    {
        if (order.LineItems.Count == 0)
        {
            return ResultError.Rejected(OrderSyncErrors.WithoutLines, $"El pedido {order.Id} no tiene productos.");
        }

        foreach (var line in order.LineItems)
        {
            if (string.IsNullOrWhiteSpace(line.Sku))
            {
                return ResultError.Rejected(
                    OrderSyncErrors.LineWithoutSku,
                    $"El producto '{line.Name}' del pedido {order.Id} no tiene SKU: no se puede asociar a un artículo de SAP.");
            }

            if (line.Quantity <= 0)
            {
                return ResultError.Rejected(
                    OrderSyncErrors.InvalidQuantity,
                    $"El producto '{line.Name}' (SKU {line.Sku}) del pedido {order.Id} tiene una cantidad inválida: {line.Quantity}.");
            }
        }

        return null;
    }

    private async Task<Result<string>> ResolveCustomerAsync(WooCommerceOrder order, CancellationToken cancellationToken)
    {
        var email = NullIfBlank(order.Billing.Email) is { } rawEmail ? CustomerKey.NormalizeEmail(rawEmail) : null;

        var key = order.IsGuest
            ? (email is null ? null : CustomerKey.ForGuest(email))
            : CustomerKey.ForRegistered(order.CustomerId);

        if (key is null)
        {
            return FailRejected<string>(
                OrderSyncErrors.UnidentifiableCustomer,
                $"El pedido {order.Id} no tiene un cliente identificable: se compró como invitado y sin correo.");
        }

        var mapped = await mappings.FindCardCodeAsync(key, cancellationToken);
        if (mapped is not null)
        {
            return Result.Success(mapped);
        }

        // El RUT, si el pedido lo trae, debe ser válido antes de buscar o crear nada en SAP.
        Rut? taxId = null;
        var rawTaxId = options.TaxIdMetaKey.Length > 0 ? order.GetMeta(options.TaxIdMetaKey) : null;
        if (rawTaxId is not null)
        {
            if (!Rut.TryParse(rawTaxId, out var rut))
            {
                return FailRejected<string>(
                    OrderSyncErrors.InvalidTaxId,
                    $"El RUT del pedido {order.Id} no es válido (dígito verificador incorrecto). Corrígelo en la tienda y reintenta; el valor está en el detalle del job con includePayload=true.");
            }

            taxId = rut;
        }

        var search = taxId is { } knownTaxId
            ? await sap.FindCustomerByTaxIdAsync(knownTaxId, cancellationToken)
            : email is not null
                ? await sap.FindCustomerByEmailAsync(email, cancellationToken)
                : Result.Success<SapCustomer?>(null);
        if (search.IsFailure)
        {
            return Result.Failure<string>(search.Error!);
        }

        if (search.Value is { } existing)
        {
            if (!existing.IsActive)
            {
                return FailRejected<string>(
                    OrderSyncErrors.InactiveCustomer,
                    $"El cliente de SAP asociado al pedido {order.Id} está inhabilitado o congelado: habilítalo en SAP y reintenta el job.");
            }

            await mappings.SaveCustomerMappingAsync(key, existing.CardCode, cancellationToken);
            return Result.Success(existing.CardCode);
        }

        var code = ChooseCardCode(order, taxId, email);
        if (code is null)
        {
            return FailRejected<string>(
                OrderSyncErrors.UnidentifiableCustomer,
                $"No se pudo asignar un código de cliente de SAP al pedido {order.Id}.");
        }

        var ensured = await sap.EnsureCustomerAsync(NewCustomer(order, code, taxId, email), cancellationToken);
        if (ensured.IsFailure)
        {
            return Result.Failure<string>(ensured.Error!);
        }

        await mappings.SaveCustomerMappingAsync(key, ensured.Value.CardCode, cancellationToken);
        LogCustomerCreated(order.Id);
        return Result.Success(ensured.Value.CardCode);
    }

    /// <summary>El código del cliente nuevo: por RUT si lo hay; si no, por su cuenta en la tienda o por su correo.</summary>
    private static string? ChooseCardCode(WooCommerceOrder order, Rut? taxId, string? email)
    {
        if (taxId is { } rut)
        {
            return SapCardCodes.ForRut(rut);
        }

        if (!order.IsGuest && order.CustomerId <= SapCardCodes.MaxStoreCustomerId)
        {
            return SapCardCodes.ForStoreCustomer(order.CustomerId);
        }

        return email is null ? null : SapCardCodes.ForGuest(email);
    }

    private static NewSapCustomer NewCustomer(WooCommerceOrder order, string cardCode, Rut? taxId, string? email)
    {
        var billing = order.Billing;
        var name = NullIfBlank(billing.Company) ?? NullIfBlank(billing.FullName) ?? email ?? $"Cliente pedido {order.Id}";

        var addresses = new List<SapAddress>();
        if (ToAddress("Facturación", SapAddressKind.Billing, billing) is { } billingAddress)
        {
            addresses.Add(billingAddress);
        }

        if (ToAddress("Despacho", SapAddressKind.Shipping, order.Shipping) is { } shippingAddress)
        {
            addresses.Add(shippingAddress);
        }

        return new NewSapCustomer(
            cardCode,
            Truncate(name, MaxNameLength),
            taxId,
            email,
            NullIfBlank(billing.Phone) is { } phone ? Truncate(phone, MaxPhoneLength) : null,
            addresses);
    }

    private static SapAddress? ToAddress(string name, SapAddressKind kind, WooCommerceAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.Address1))
        {
            return null;
        }

        var street = string.Join(", ", new[] { address.Address1, address.Address2 }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new SapAddress(
            name,
            kind,
            Truncate(street, MaxStreetLength),
            NullIfBlank(address.City) is { } city ? Truncate(city, MaxStreetLength) : null,
            NullIfBlank(address.Country),
            NullIfBlank(address.Postcode) is { } postcode ? Truncate(postcode, MaxPostalCodeLength) : null);
    }

    private async Task<Result<IReadOnlyList<NewSapOrderLine>>> BuildLinesAsync(WooCommerceOrder order, CancellationToken cancellationToken)
    {
        var itemCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<NewSapOrderLine>(order.LineItems.Count);

        foreach (var line in order.LineItems)
        {
            var sku = line.Sku.Trim();

            if (!itemCodes.TryGetValue(sku, out var itemCode))
            {
                var resolved = await ResolveItemCodeAsync(order, line, sku, cancellationToken);
                if (resolved.IsFailure)
                {
                    return Result.Failure<IReadOnlyList<NewSapOrderLine>>(resolved.Error!);
                }

                itemCode = resolved.Value;
                itemCodes[sku] = itemCode;
            }

            // El total de la línea ya incluye los cupones y excluye impuestos: dividirlo por la cantidad da el precio
            // que efectivamente pagó el cliente por unidad.
            var unitPrice = Math.Round(line.Total / line.Quantity, PriceDecimals, MidpointRounding.AwayFromZero);
            lines.Add(new NewSapOrderLine(itemCode, line.Quantity, unitPrice));
        }

        return Result.Success<IReadOnlyList<NewSapOrderLine>>(lines);
    }

    private async Task<Result<string>> ResolveItemCodeAsync(
        WooCommerceOrder order,
        WooCommerceLineItem line,
        string sku,
        CancellationToken cancellationToken)
    {
        var mapped = await mappings.FindItemCodeAsync(sku, cancellationToken);
        if (mapped is not null)
        {
            return Result.Success(mapped);
        }

        // Sin mapeo, se asume que el SKU de la tienda es el código del artículo, pero solo si SAP lo conoce.
        var item = await sap.GetItemAsync(sku, cancellationToken);
        if (item.IsFailure)
        {
            return Result.Failure<string>(item.Error!);
        }

        if (item.Value is null)
        {
            return FailRejected<string>(
                OrderSyncErrors.UnknownSku,
                $"El SKU '{sku}' ('{line.Name}') del pedido {order.Id} no tiene mapeo ni existe como artículo en SAP.");
        }

        if (!item.Value.IsActive)
        {
            return FailRejected<string>(
                OrderSyncErrors.InactiveItem,
                $"El artículo {item.Value.ItemCode} ('{line.Name}') está inhabilitado o congelado en SAP.");
        }

        await mappings.SaveProductMappingAsync(sku, item.Value.ItemCode, cancellationToken);
        return Result.Success(item.Value.ItemCode);
    }

    private static string BuildComments(WooCommerceOrder order)
    {
        var comments = $"Pedido WooCommerce #{order.Number}";
        if (!string.IsNullOrWhiteSpace(order.CustomerNote))
        {
            comments += $". Nota del cliente: {order.CustomerNote}";
        }

        return Truncate(comments, MaxCommentsLength);
    }

    /// <summary>La fecha en que el cliente compró, en la zona horaria de la tienda (a las 23:30 en Chile ya es mañana en UTC).</summary>
    private DateOnly? ToStoreDate(WooCommerceOrder order) =>
        order.DateCreated is { } created
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(created, _timeZone).DateTime)
            : null;

    private void WarnIfTotalsDiffer(WooCommerceOrder order, decimal? storeTotal, decimal sapTotal)
    {
        var store = storeTotal ?? order.Total;
        var difference = store - sapTotal;

        if (Math.Abs(difference) > SyncJob.TotalTolerance)
        {
            LogTotalsDiffer(order.Id, store, sapTotal, difference, order.TotalTax, order.ShippingTotal, order.DiscountTotal);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static Result<OrderSyncOutcome> Rejected(string code, string message) =>
        Result.Failure<OrderSyncOutcome>(ResultError.Rejected(code, message));

    private static Result<T> FailRejected<T>(string code, string message) =>
        Result.Failure<T>(ResultError.Rejected(code, message));

    [LoggerMessage(Level = LogLevel.Information, Message = "El pedido {OrderId} ya tenía la orden {DocEntry} (nº {DocNum}) en SAP; no se crea otra")]
    private partial void LogOrderAlreadyExists(long orderId, int docEntry, int docNum);

    // Los logs no llevan datos personales: el CardCode de un cliente nuevo se deriva de su RUT, así que no se registra.
    [LoggerMessage(Level = LogLevel.Information, Message = "Cliente nuevo creado en SAP para el pedido {OrderId}")]
    private partial void LogCustomerCreated(long orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orden {DocEntry} (nº {DocNum}) creada en SAP para el pedido {OrderId}")]
    private partial void LogOrderCreated(long orderId, int docEntry, int docNum);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "El total de la tienda ({StoreTotal}) difiere del de SAP ({SapTotal}) en el pedido {OrderId}: diferencia de {Difference}. " +
                  "La orden de SAP excluye envío e impuestos (impuestos de la tienda: {Tax}, envío: {Shipping}, descuentos: {Discount})")]
    private partial void LogTotalsDiffer(long orderId, decimal storeTotal, decimal sapTotal, decimal difference, decimal tax, decimal shipping, decimal discount);
}
