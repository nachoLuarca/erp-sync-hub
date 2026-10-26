using System.Net;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Jobs;
using Microsoft.Extensions.Options;
using SapB1.Client;
using SapB1.Client.Models;

namespace ErpSyncHub.Infrastructure.Sap;

/// <summary>
/// Adaptador de <see cref="ISapGateway"/> sobre <c>SapB1.Client</c>. Convierte las excepciones del cliente en
/// <see cref="Result"/> (ADR 0006): un rechazo de SAP es definitivo, mientras que un error del servidor, un problema de
/// sesión, de red o un tiempo de espera agotado son pasajeros y la Fase 4 los reintentará.
/// </summary>
internal sealed class SapGateway(ISapB1Client sap, IOptions<SapGatewayOptions> options) : ISapGateway
{
    public Task<Result<SapCustomer?>> FindCustomerByTaxIdAsync(Rut taxId, CancellationToken cancellationToken)
    {
        // El RUT se busca en el formato con que SAP lo guarda (SAP:TaxIdFormat), no en cualquiera: así una discrepancia
        // de configuración no crea clientes duplicados en silencio.
        var filter = SapFilter.And(
            SapFilter.Equal("CardType", CardTypes.Customer),
            SapFilter.Equal("FederalTaxID", taxId.Format(options.Value.TaxIdFormat)));

        return FindCustomerAsync(filter, cancellationToken);
    }

    public Task<Result<SapCustomer?>> FindCustomerByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var filter = SapFilter.And(
            SapFilter.Equal("CardType", CardTypes.Customer),
            SapFilter.Equal("EmailAddress", email));

        return FindCustomerAsync(filter, cancellationToken);
    }

    public Task<Result<SapCustomer?>> GetCustomerAsync(string cardCode, CancellationToken cancellationToken) =>
        ExecuteAsync(
            async () => ToCustomer(await sap.BusinessPartners.GetAsync(cardCode, cancellationToken)),
            cancellationToken);

    public async Task<Result<SapCustomer>> EnsureCustomerAsync(NewSapCustomer customer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var created = await ExecuteAsync<SapCustomer?>(
            async () =>
            {
                try
                {
                    return ToCustomer(await sap.BusinessPartners.CreateAsync(ToRequest(customer, options.Value.TaxIdFormat), cancellationToken));
                }
                catch (SapB1ApiException exception) when (exception.IsAlreadyExists)
                {
                    return null;
                }
            },
            cancellationToken);

        if (created.IsFailure)
        {
            return Result.Failure<SapCustomer>(created.Error!);
        }

        if (created.Value is { } newCustomer)
        {
            return Result.Success(newCustomer);
        }

        // El CardCode es determinista: si ya existe, es el mismo cliente (por ejemplo, el job se cortó a medias).
        var existing = await GetCustomerAsync(customer.CardCode, cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure<SapCustomer>(existing.Error!);
        }

        return existing.Value is { } found
            ? Result.Success(found)
            : Result.Failure<SapCustomer>(ResultError.Transient(
                SapErrors.CustomerConflict,
                $"SAP informó que el cliente {customer.CardCode} ya existe, pero no se pudo leer."));
    }

    public Task<Result<SapItem?>> GetItemAsync(string itemCode, CancellationToken cancellationToken) =>
        ExecuteAsync(
            async () =>
            {
                var item = await sap.Items.GetAsync(itemCode, cancellationToken);
                return item is null
                    ? null
                    : new SapItem(
                        item.ItemCode,
                        item.ItemName ?? string.Empty,
                        item.Valid != YesNo.No && item.Frozen != YesNo.Yes);
            },
            cancellationToken);

    public Task<Result<IReadOnlyList<SapInventoryItem>>> GetInventoryPageAsync(
        DateOnly? changedSince,
        string? afterItemCode,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var conditions = new List<string>();
        if (changedSince is { } since)
        {
            conditions.Add(SapFilter.GreaterOrEqual("UpdateDate", since));
        }

        if (afterItemCode is not null)
        {
            conditions.Add(SapFilter.Greater("ItemCode", afterItemCode));
        }

        var query = new SapQuery
        {
            Filter = conditions.Count == 0 ? null : SapFilter.And([.. conditions]),
            OrderBy = "ItemCode",
            Top = pageSize,
            Select = ["ItemCode", "Valid", "Frozen", "UpdateDate", "UpdateTime", "ItemPrices", "ItemWarehouseInfoCollection"],
        };

        return ExecuteAsync<IReadOnlyList<SapInventoryItem>>(
            async () =>
            {
                var items = new List<SapInventoryItem>(pageSize);
                await foreach (var item in sap.Items.QueryAsync(query, cancellationToken))
                {
                    items.Add(ToInventoryItem(item));
                }

                return items;
            },
            cancellationToken);
    }

    public Task<Result<SapOrderReference?>> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        ExecuteAsync(
            async () =>
            {
                var query = new SapQuery
                {
                    Filter = SapFilter.Equal("NumAtCard", reference),
                    OrderBy = "DocEntry",
                    Top = 1,
                    Select = ["DocEntry", "DocNum", "CardCode", "DocTotal"],
                };

                await foreach (var order in sap.Orders.QueryAsync(query, cancellationToken))
                {
                    return ToReference(order);
                }

                return null;
            },
            cancellationToken);

    public Task<Result<SapOrderReference>> CreateOrderAsync(NewSapOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        return ExecuteAsync(
            async () =>
            {
                var created = await sap.Orders.CreateAsync(
                    new CreateOrderRequest
                    {
                        CardCode = order.CardCode,
                        NumAtCard = order.Reference,
                        Comments = order.Comments,
                        DocDate = order.DocumentDate,
                        DocumentLines =
                        [
                            .. order.Lines.Select(line => new CreateOrderLine
                            {
                                ItemCode = line.ItemCode,
                                Quantity = line.Quantity,
                                UnitPrice = line.UnitPrice,
                            }),
                        ],
                    },
                    cancellationToken);

                return ToReference(created);
            },
            cancellationToken);
    }

    private Task<Result<SapCustomer?>> FindCustomerAsync(string filter, CancellationToken cancellationToken) =>
        ExecuteAsync(
            async () =>
            {
                var query = new SapQuery { Filter = filter, OrderBy = "CardCode", Top = 1 };

                await foreach (var partner in sap.BusinessPartners.QueryAsync(query, cancellationToken))
                {
                    return ToCustomer(partner);
                }

                return null;
            },
            cancellationToken);

    /// <summary>
    /// Ejecuta una operación contra SAP y traduce sus excepciones. La cancelación solicitada por quien llama no se
    /// traduce: se propaga, para que un apagado ordenado no se confunda con un fallo de SAP.
    /// </summary>
    private static async Task<Result<T>> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return Result.Success(await operation());
        }
        catch (SapB1ApiException exception)
        {
            return Result.Failure<T>(Translate(exception));
        }
        catch (SapB1SessionException exception)
        {
            return Result.Failure<T>(ResultError.Transient(
                SapErrors.Session,
                $"No se pudo iniciar o mantener la sesión en SAP{Describe(exception.StatusCode, exception.SapErrorCode, exception.SapErrorMessage)}"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // Un IOException suelto es una conexión cortada a mitad de la respuesta: igual de pasajero que no poder conectar.
            return Result.Failure<T>(ResultError.Transient(
                SapErrors.Unreachable,
                $"No se pudo conectar con SAP: {Describe(exception)}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El HttpClient agotó su tiempo de espera; no fue una cancelación pedida por el servicio.
            return Result.Failure<T>(ResultError.Transient(SapErrors.Timeout, "SAP no respondió dentro del tiempo de espera."));
        }
    }

    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    private static ResultError Translate(SapB1ApiException exception)
    {
        var detail = Describe(exception.StatusCode, exception.SapErrorCode, exception.SapErrorMessage);
        var status = (int)exception.StatusCode;

        // 5xx y una respuesta exitosa que no se pudo leer (por ejemplo, la página de un proxy) son pasajeros, igual que
        // 408 (tiempo agotado), 425 (muy pronto) y 429 (demasiadas solicitudes), que son 4xx.
        var transient = status is >= 500 or < 400 or 408 or 425 or 429;

        return transient
            ? ResultError.Transient(SapErrors.ServerError, $"SAP no pudo atender la solicitud{detail}")
            : ResultError.Rejected(SapErrors.Rejected, $"SAP rechazó la operación{detail}");
    }

    private static string Describe(HttpStatusCode? status, int? code, string? message)
    {
        var parts = new List<string>();
        if (status is { } httpStatus)
        {
            parts.Add($"HTTP {(int)httpStatus}");
        }

        if (code is { } sapCode)
        {
            parts.Add($"código {sapCode}");
        }

        var prefix = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : string.Empty;
        return string.IsNullOrWhiteSpace(message) ? $"{prefix}." : $"{prefix}: {message}";
    }

    private static SapCustomer? ToCustomer(BusinessPartner? partner) =>
        partner is null
            ? null
            : new SapCustomer(
                partner.CardCode,
                partner.CardName ?? string.Empty,
                partner.FederalTaxID,
                partner.EmailAddress,
                partner.Valid != YesNo.No && partner.Frozen != YesNo.Yes);

    private static SapInventoryItem ToInventoryItem(Item item) =>
        new(
            item.ItemCode,
            item.Valid != YesNo.No && item.Frozen != YesNo.Yes,
            item.UpdateDate,
            item.UpdateTime,
            [.. item.ItemWarehouseInfoCollection.Select(w => new SapWarehouseStock(w.WarehouseCode, w.InStock, w.Committed))],
            [.. item.ItemPrices.Select(p => new SapItemPrice(p.PriceList, p.Price))]);

    private static SapOrderReference ToReference(Order order) =>
        new(order.DocEntry, order.DocNum, order.CardCode, order.DocTotal);

    private static CreateBusinessPartnerRequest ToRequest(NewSapCustomer customer, TaxIdFormat taxIdFormat) => new()
    {
        CardCode = customer.CardCode,
        CardName = customer.CardName,
        CardType = CardTypes.Customer,
        FederalTaxID = customer.TaxId?.Format(taxIdFormat),
        EmailAddress = customer.Email,
        Phone1 = customer.Phone,
        BPAddresses =
        [
            .. customer.Addresses.Select(address => new BpAddress
            {
                AddressName = address.Name,
                AddressType = address.Kind == SapAddressKind.Billing ? AddressTypes.BillTo : AddressTypes.ShipTo,
                Street = address.Street,
                City = address.City,
                Country = address.Country,
                ZipCode = address.PostalCode,
            }),
        ],
    };
}
