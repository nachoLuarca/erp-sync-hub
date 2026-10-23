using Microsoft.Extensions.Options;
using SapB1.Client;

namespace ErpSyncHub.Api.Sync;

/// <summary>Configuración del worker que procesa los jobs (sección <c>Worker</c>).</summary>
public sealed class SyncWorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Cota inferior del lease: debajo de esto no queda margen razonable para hablar con SAP.</summary>
    public static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(30);

    /// <summary>Permite apagar el worker, por ejemplo para ejecutar la Api sin procesar jobs. Por defecto, activo.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cada cuánto consulta la cola un worker que no encontró trabajo. Por defecto, 2 segundos.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Cantidad de jobs que se procesan a la vez en este proceso. Por defecto, 2.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>
    /// Cuánto tiempo conserva un worker un job tomado antes de que otro pueda retomarlo. Debe ser mayor que el tiempo de
    /// espera de las llamadas a SAP. Por defecto, 5 minutos.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tiempo máximo de procesamiento de un job: el lease menos un margen. Al ser menor que el lease, un job nunca sigue
    /// ejecutándose cuando otro worker ya pudo haberlo retomado.
    /// </summary>
    public TimeSpan JobTimeout => LeaseDuration - TimeSpan.FromSeconds(Math.Max(5, LeaseDuration.TotalSeconds * 0.1));
}

internal sealed class SyncWorkerOptionsValidator(IOptions<SapB1Options> sap) : IValidateOptions<SyncWorkerOptions>
{
    private const int MaxConcurrency = 32;

    public ValidateOptionsResult Validate(string? name, SyncWorkerOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval < TimeSpan.FromMilliseconds(10) || options.PollInterval > TimeSpan.FromHours(1))
        {
            errors.Add("Worker:PollInterval debe estar entre 10 milisegundos y 1 hora.");
        }

        if (options.Concurrency is < 1 or > MaxConcurrency)
        {
            errors.Add($"Worker:Concurrency debe estar entre 1 y {MaxConcurrency}.");
        }

        if (options.LeaseDuration < SyncWorkerOptions.MinimumLease)
        {
            errors.Add($"Worker:LeaseDuration debe ser de al menos {SyncWorkerOptions.MinimumLease.TotalSeconds:0} segundos.");
        }

        // Si el lease fuera menor que el tiempo de espera de SAP, otro worker podría retomar el job mientras el primero
        // todavía espera la respuesta, y SAP podría recibir dos veces la misma orden.
        var sapTimeout = sap.Value.RequestTimeout;
        if (options.LeaseDuration <= sapTimeout)
        {
            errors.Add(
                $"Worker:LeaseDuration ({options.LeaseDuration}) debe ser mayor que el tiempo de espera de las llamadas a SAP " +
                $"(SAP:RequestTimeout = {sapTimeout}).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
