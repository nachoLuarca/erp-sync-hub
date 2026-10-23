using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sync;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Sync;

/// <summary>
/// Procesa los jobs de la cola: varios bucles independientes toman un job a la vez (<c>FOR UPDATE SKIP LOCKED</c>), lo
/// ejecutan con <see cref="SyncJobRunner"/> y vuelven a tomar. La lógica de cada job vive en Application; este servicio solo
/// reparte el trabajo y sobrevive a los fallos de la base de datos.
/// </summary>
internal sealed partial class SyncWorker(
    IServiceScopeFactory scopes,
    IOptions<SyncWorkerOptions> options,
    TimeProvider time,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private readonly SyncWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled();
            return;
        }

        LogStarting(_options.Concurrency, _options.PollInterval, _options.LeaseDuration);

        var instance = $"{Truncate(Environment.MachineName, 60)}-{Environment.ProcessId}";
        var loops = Enumerable.Range(1, _options.Concurrency)
            .Select(number => RunLoopAsync($"{instance}-{number}", stoppingToken));

        await Task.WhenAll(loops);
        LogStopped();
    }

    private async Task RunLoopAsync(string workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunNextJobAsync(workerId, stoppingToken))
                {
                    await Task.Delay(_options.PollInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Una falla de la base de datos (o del cierre de un job) no debe matar al worker: se reintenta en el siguiente ciclo.
                LogLoopFailure(exception, workerId);
                try
                {
                    await Task.Delay(_options.PollInterval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Toma y procesa un job. Devuelve <see langword="false"/> si no había ninguno.</summary>
    private async Task<bool> RunNextJobAsync(string workerId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var claimed = await queue.ClaimAsync(workerId, maxJobs: 1, _options.LeaseDuration, stoppingToken);
        if (claimed.Count == 0)
        {
            return false;
        }

        var runner = scope.ServiceProvider.GetRequiredService<SyncJobRunner>();
        await runner.RunAsync(claimed[0], _options.JobTimeout, stoppingToken);
        return true;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker deshabilitado (Worker:Enabled=false): no se procesarán jobs")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker iniciado: {Concurrency} procesos en paralelo, consulta cada {PollInterval}, lease de {Lease}")]
    private partial void LogStarting(int concurrency, TimeSpan pollInterval, TimeSpan lease);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker detenido")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Error, Message = "El worker {WorkerId} tuvo un error y lo reintentará")]
    private partial void LogLoopFailure(Exception exception, string workerId);
}
