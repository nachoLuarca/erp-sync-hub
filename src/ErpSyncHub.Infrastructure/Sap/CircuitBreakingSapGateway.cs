using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Infrastructure.Sap;

/// <summary>
/// Pone el circuit breaker delante de SAP (ADR 0011). Con el circuito cerrado, cada llamada pasa y su resultado se informa: las fallas
/// pasajeras de infraestructura (sin conexión, error del servidor, tiempo agotado, sesión) cuentan para abrirlo, las credenciales
/// rechazadas lo abren de inmediato, y cualquier otra respuesta de SAP —incluso un rechazo de negocio— prueba que SAP responde y reinicia
/// la racha. Con el circuito abierto o semiabierto no se llama a SAP: la llamada falla al instante con <see cref="CircuitErrors.Open"/>.
/// </summary>
internal sealed partial class CircuitBreakingSapGateway(
    ISapGateway inner,
    CircuitBreakerService circuit,
    ILogger<CircuitBreakingSapGateway> logger) : ISapGateway
{
    public Task<Result<SapCustomer?>> FindCustomerByTaxIdAsync(Rut taxId, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.FindCustomerByTaxIdAsync(taxId, cancellationToken), cancellationToken);

    public Task<Result<SapCustomer?>> FindCustomerByEmailAsync(string email, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.FindCustomerByEmailAsync(email, cancellationToken), cancellationToken);

    public Task<Result<SapCustomer?>> GetCustomerAsync(string cardCode, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.GetCustomerAsync(cardCode, cancellationToken), cancellationToken);

    public Task<Result<SapCustomer>> EnsureCustomerAsync(NewSapCustomer customer, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.EnsureCustomerAsync(customer, cancellationToken), cancellationToken);

    public Task<Result<SapItem?>> GetItemAsync(string itemCode, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.GetItemAsync(itemCode, cancellationToken), cancellationToken);

    public Task<Result<IReadOnlyList<SapInventoryItem>>> GetInventoryPageAsync(
        DateOnly? changedSince,
        string? afterItemCode,
        int pageSize,
        CancellationToken cancellationToken) =>
        GuardAsync(() => inner.GetInventoryPageAsync(changedSince, afterItemCode, pageSize, cancellationToken), cancellationToken);

    public Task<Result<SapOrderReference?>> FindOrderByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.FindOrderByReferenceAsync(reference, cancellationToken), cancellationToken);

    public Task<Result<SapOrderReference>> CreateOrderAsync(NewSapOrder order, CancellationToken cancellationToken) =>
        GuardAsync(() => inner.CreateOrderAsync(order, cancellationToken), cancellationToken);

    private async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        if (!circuit.Enabled)
        {
            return await call();
        }

        var before = await circuit.GetAsync(CircuitNames.Sap, cancellationToken);
        if (!before.AllowsWork)
        {
            return Result.Failure<T>(ResultError.Transient(
                CircuitErrors.Open,
                $"El circuito de SAP está {Describe(before)}: no se llamó a SAP. Se reintentará cuando la prueba pase."));
        }

        var result = await call();
        await ReportAsync(result.Error, before, cancellationToken);
        return result;
    }

    /// <summary>Informa el resultado al circuito. Una falla del propio registro no debe cambiar lo que SAP respondió.</summary>
    private async Task ReportAsync(ResultError? error, CircuitSnapshot before, CancellationToken cancellationToken)
    {
        try
        {
            switch (error)
            {
                case null or { Kind: not ErrorKind.Transient }:
                    // SAP respondió con sentido (aunque haya rechazado la operación): la conexión está bien.
                    await circuit.ReportSuccessAsync(CircuitNames.Sap, before, cancellationToken);
                    break;

                case { Code: SapErrors.InvalidCredentials }:
                    await circuit.ReportCredentialsRejectedAsync(CircuitNames.Sap, error.Code, error.Message, cancellationToken);
                    break;

                case { Code: SapErrors.Session or SapErrors.Unreachable or SapErrors.Timeout or SapErrors.ServerError }:
                    await circuit.ReportTransientFailureAsync(CircuitNames.Sap, error.Code, error.Message, cancellationToken);
                    break;

                default:
                    break; // otro fallo pasajero que no dice nada de la salud de SAP
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogReportFailed(exception);
        }
    }

    private static string Describe(CircuitSnapshot snapshot) =>
        snapshot.State == ErpSyncHub.Domain.Resilience.CircuitState.HalfOpen ? "semiabierto (probando)" : "abierto";

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo informar el resultado de SAP al circuit breaker")]
    private partial void LogReportFailed(Exception exception);
}
