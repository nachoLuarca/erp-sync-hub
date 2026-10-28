using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.UnitTests.Support;

/// <summary>
/// Un SAP en memoria para probar la sincronización sin red: clientes, artículos y órdenes reales de mentira, un registro de
/// las llamadas recibidas y la posibilidad de hacer fallar cualquier operación.
/// </summary>
internal sealed class FakeSapGateway : ISapGateway
{
    private readonly Dictionary<string, ResultError> _failures = [];
    private int _nextDocEntry = 1;

    public List<SapCustomer> Customers { get; } = [];

    public Dictionary<string, SapItem> Items { get; } = [];

    public List<(string Reference, NewSapOrder Order, SapOrderReference Created)> Orders { get; } = [];

    public List<NewSapCustomer> CreatedCustomers { get; } = [];

    /// <summary>Nombres de las operaciones recibidas, en orden.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Se ejecuta justo después de crear una orden; permite simular que algo ocurre en ese instante.</summary>
    public Action? AfterCreateOrder { get; set; }

    /// <summary>Se ejecuta al recibir cada operación, con su nombre; permite observar el estado del servicio en ese instante.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Último correo recibido en una búsqueda.</summary>
    public string? LastEmailSearched { get; private set; }

    public void FailOn(string operation, ResultError error) => _failures[operation] = error;

    public void ClearFailures() => _failures.Clear();

    public void AddItem(string itemCode, string name = "Artículo", bool active = true) =>
        Items[itemCode] = new SapItem(itemCode, name, active);

    public Task<Result<SapCustomer?>> FindCustomerByTaxIdAsync(Rut taxId, CancellationToken cancellationToken) =>
        Run<SapCustomer?>(
            nameof(FindCustomerByTaxIdAsync),
            () => Customers.FirstOrDefault(customer =>
                customer.TaxId is not null
                && Rut.TryParse(customer.TaxId, out var stored)
                && stored == taxId));

    public Task<Result<SapCustomer?>> FindCustomerByEmailAsync(string email, CancellationToken cancellationToken)
    {
        LastEmailSearched = email;
        return Run<SapCustomer?>(nameof(FindCustomerByEmailAsync), () => Customers.FirstOrDefault(customer => customer.Email == email));
    }

    public Task<Result<SapCustomer?>> GetCustomerAsync(string cardCode, CancellationToken cancellationToken) =>
        Run<SapCustomer?>(nameof(GetCustomerAsync), () => Customers.FirstOrDefault(customer => customer.CardCode == cardCode));

    public Task<Result<SapCustomer>> EnsureCustomerAsync(NewSapCustomer customer, CancellationToken cancellationToken) =>
        Run(
            nameof(EnsureCustomerAsync),
            () =>
            {
                // Como el real: crear un cliente que ya existe devuelve el existente.
                if (Customers.FirstOrDefault(existing => existing.CardCode == customer.CardCode) is { } already)
                {
                    return already;
                }

                CreatedCustomers.Add(customer);
                var created = new SapCustomer(customer.CardCode, customer.CardName, customer.TaxId?.Formatted, customer.Email, IsActive: true);
                Customers.Add(created);
                return created;
            });

    public Task<Result<SapItem?>> GetItemAsync(string itemCode, CancellationToken cancellationToken) =>
        Run<SapItem?>(nameof(GetItemAsync), () => Items.GetValueOrDefault(itemCode));

    /// <summary>Artículos del maestro que lee la sincronización de stock y precios, en cualquier orden.</summary>
    public List<SapInventoryItem> Inventory { get; } = [];

    /// <summary>Se ejecuta después de cada lectura del maestro, con el número de lecturas hechas hasta ahora.</summary>
    public Action<int>? AfterInventoryRead { get; set; }

    /// <summary>Parámetros de cada lectura del maestro, en orden.</summary>
    public List<(DateOnly? ChangedSince, string? AfterItemCode, int PageSize)> InventoryReads { get; } = [];

    public void AddInventoryItem(
        string itemCode,
        decimal inStock = 0,
        decimal committed = 0,
        decimal? price = null,
        bool active = true,
        string warehouse = "01",
        int priceList = 1,
        DateOnly? updatedOn = null) =>
        Inventory.Add(new SapInventoryItem(
            itemCode,
            active,
            updatedOn,
            UpdatedAt: null,
            [new SapWarehouseStock(warehouse, inStock, committed)],
            [new SapItemPrice(priceList, price)]));

    public Task<Result<IReadOnlyList<SapInventoryItem>>> GetInventoryPageAsync(
        DateOnly? changedSince,
        string? afterItemCode,
        int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InventoryReads.Add((changedSince, afterItemCode, pageSize));

        var page = Run<IReadOnlyList<SapInventoryItem>>(
            nameof(GetInventoryPageAsync),
            () => [.. Inventory
                .Where(item => changedSince is null || (item.UpdatedOn is { } on && on >= changedSince))
                .Where(item => afterItemCode is null || string.CompareOrdinal(item.ItemCode, afterItemCode) > 0)
                .OrderBy(item => item.ItemCode, StringComparer.Ordinal)
                .Take(pageSize)]);
        AfterInventoryRead?.Invoke(InventoryReads.Count);
        return page;
    }

    public Task<Result<SapOrderReference?>> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        Run<SapOrderReference?>(
            nameof(FindOrderByReferenceAsync),
            () => Orders.FirstOrDefault(order => order.Reference == reference).Created);

    /// <summary>Como el SAP real: rechaza una orden cuyo cliente o artículos no existen. Por defecto acepta cualquier orden.</summary>
    public bool RejectUnknownReferences { get; set; }

    public Task<Result<SapOrderReference>> CreateOrderAsync(NewSapOrder order, CancellationToken cancellationToken)
    {
        if (RejectUnknownReferences)
        {
            if (!Customers.Exists(customer => customer.CardCode == order.CardCode))
            {
                Calls.Add(nameof(CreateOrderAsync));
                return Task.FromResult(Result.Failure<SapOrderReference>(ResultError.Rejected(SapErrors.Rejected, "SAP rechazó la operación (HTTP 400): el cliente no existe")));
            }

            if (order.Lines.Any(line => !Items.ContainsKey(line.ItemCode)))
            {
                Calls.Add(nameof(CreateOrderAsync));
                return Task.FromResult(Result.Failure<SapOrderReference>(ResultError.Rejected(SapErrors.Rejected, "SAP rechazó la operación (HTTP 400): un artículo no existe")));
            }
        }

        return CreateOrderCoreAsync(order);
    }

    private Task<Result<SapOrderReference>> CreateOrderCoreAsync(NewSapOrder order) =>
        Run(
            nameof(CreateOrderAsync),
            () =>
            {
                var total = order.Lines.Sum(line => line.Quantity * line.UnitPrice);
                var created = new SapOrderReference(_nextDocEntry, 1000 + _nextDocEntry, order.CardCode, total);
                _nextDocEntry++;
                Orders.Add((order.Reference, order, created));
                AfterCreateOrder?.Invoke();
                return created;
            });

    private Task<Result<T>> Run<T>(string operation, Func<T> action)
    {
        Calls.Add(operation);
        OnCall?.Invoke(operation);
        return Task.FromResult(_failures.TryGetValue(operation, out var error)
            ? Result.Failure<T>(error)
            : Result.Success(action()));
    }
}
