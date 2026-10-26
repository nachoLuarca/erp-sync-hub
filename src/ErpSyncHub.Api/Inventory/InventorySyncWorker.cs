using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Inventory;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Inventory;

/// <summary>
/// Dispara la sincronización de stock y precios cada <see cref="InventorySyncOptions.Interval"/>. Corre una vez al iniciar y
/// luego a intervalos fijos. La lógica vive en Application (<see cref="InventorySyncRunner"/>), que también garantiza que
/// nunca haya dos ejecuciones a la vez; este servicio solo marca el ritmo y sobrevive a los fallos.
/// </summary>
internal sealed partial class InventorySyncWorker(
    IServiceScopeFactory scopes,
    IOptions<InventorySyncOptions> options,
    TimeProvider time,
    ILogger<InventorySyncWorker> logger) : BackgroundService
{
    private readonly InventorySyncOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService ejecuta este método de forma síncrona hasta su primer await, dentro del arranque del host: se cede el hilo
        // para que ningún servicio se resuelva ni se lance trabajo antes de que el host termine de iniciar.
        await Task.Yield();

        if (!_options.Enabled)
        {
            LogDisabled();
            return;
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<IStoreGateway>().IsConfigured)
            {
                LogStoreNotConfigured();
                return;
            }
        }

        LogStarting(_options.Interval, _options.Warehouses, _options.PriceList, _options.PriceDecimals, _options.SapPageSize);

        using var timer = new PeriodicTimer(_options.Interval, time);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));

        LogStopped();
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<InventorySyncRunner>();

            // El resultado ya quedó registrado y logueado por el runner; aquí solo importa que un fallo no mate al servicio.
            await runner.RunAsync(InventorySyncTrigger.Scheduled, full: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado ordenado.
        }
        catch (Exception exception)
        {
            // Una falla de la base de datos no debe matar al servicio: se reintenta en el siguiente intervalo.
            LogRunFailure(exception);
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronización periódica de stock y precios desactivada (Sync__Inventory__Enabled=false)")]
    private partial void LogDisabled();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sincronización periódica de stock y precios DESACTIVADA: la tienda no está configurada. " +
                  "Define Store__WooCommerce__BaseUrl, Store__WooCommerce__ConsumerKey y Store__WooCommerce__ConsumerSecret para activarla")]
    private partial void LogStoreNotConfigured();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sincronización periódica de stock y precios activa: cada {Interval}, almacenes {Warehouses}, lista de precios {PriceList}, " +
                  "{PriceDecimals} decimales, páginas de {PageSize} artículos")]
    private partial void LogStarting(TimeSpan interval, string warehouses, int priceList, int priceDecimals, int pageSize);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronización periódica de stock y precios detenida")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Error, Message = "La sincronización periódica de stock y precios tuvo un error y se reintentará en el próximo intervalo")]
    private partial void LogRunFailure(Exception exception);
}
