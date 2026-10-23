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

    public Task<Result<SapOrderReference?>> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        Run<SapOrderReference?>(
            nameof(FindOrderByReferenceAsync),
            () => Orders.FirstOrDefault(order => order.Reference == reference).Created);

    public Task<Result<SapOrderReference>> CreateOrderAsync(NewSapOrder order, CancellationToken cancellationToken) =>
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
        return Task.FromResult(_failures.TryGetValue(operation, out var error)
            ? Result.Failure<T>(error)
            : Result.Success(action()));
    }
}
