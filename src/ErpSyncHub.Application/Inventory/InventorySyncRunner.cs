using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Application.Inventory;

/// <summary>Códigos de los errores que impiden iniciar una ejecución de la sincronización de stock y precios.</summary>
public static class InventorySyncErrors
{
    /// <summary>Otra ejecución (de esta instancia o de otra) está en curso.</summary>
    public const string AlreadyRunning = "inventory_sync.already_running";

    /// <summary>La tienda no está configurada: no hay a dónde enviar el stock y los precios.</summary>
    public const string StoreNotConfigured = "inventory_sync.store_not_configured";

    /// <summary>La ejecución se interrumpió porque el hub se estaba deteniendo.</summary>
    public const string Cancelled = "inventory_sync.cancelled";

    /// <summary>La ejecución terminó por un error inesperado.</summary>
    public const string Unexpected = "inventory_sync.unexpected";
}

/// <summary>
/// Ejecuta una sincronización de stock y precios completa y la deja registrada (ADR 0009): toma la exclusión mutua entre
/// instancias, decide desde qué fecha leer SAP, corre <see cref="InventorySyncService"/> y cierra el registro con los
/// contadores. Todo lo que ocurre durante la ejecución lleva el <c>InventoryRunId</c> en sus logs.
/// </summary>
public sealed partial class InventorySyncRunner(
    InventorySyncService service,
    IInventorySyncRunStore runs,
    IInventorySyncLock syncLock,
    IStoreGateway store,
    InventorySyncOptions options,
    TimeProvider time,
    ILogger<InventorySyncRunner> logger)
{
    private static readonly string Instance = $"{Truncate(Environment.MachineName, 60)}-{Environment.ProcessId}";

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);

    /// <summary>
    /// Corre una sincronización. Si no se pudo iniciar (tienda sin configurar u otra ejecución en curso) devuelve el
    /// error; si se inició, devuelve la ejecución cerrada, cuyo <see cref="InventorySyncRun.Status"/> dice cómo terminó.
    /// </summary>
    /// <param name="trigger">Qué la disparó.</param>
    /// <param name="full">Lee todo el maestro y envía todo, ignorando la foto y la fecha de corte.</param>
    /// <param name="cancellationToken">Detiene la ejecución, que queda registrada como fallida.</param>
    public async Task<Result<InventorySyncRun>> RunAsync(
        InventorySyncTrigger trigger,
        bool full,
        CancellationToken cancellationToken)
    {
        if (!store.IsConfigured)
        {
            return Result.Failure<InventorySyncRun>(ResultError.Conflict(
                InventorySyncErrors.StoreNotConfigured,
                "La tienda no está configurada: define Store__WooCommerce__BaseUrl, ConsumerKey y ConsumerSecret."));
        }

        await using var held = await syncLock.TryAcquireAsync(cancellationToken);
        if (held is null)
        {
            LogAlreadyRunning(trigger);
            return Result.Failure<InventorySyncRun>(ResultError.Conflict(
                InventorySyncErrors.AlreadyRunning,
                "Ya hay una sincronización de stock y precios en curso."));
        }

        // Con la exclusión tomada, cualquier ejecución que figure en curso es de una instancia que cayó.
        var abandoned = await runs.AbandonRunningAsync(cancellationToken);
        if (abandoned > 0)
        {
            LogAbandoned(abandoned);
        }

        var changedSince = full ? null : await ChangedSinceAsync(cancellationToken);
        var started = InventorySyncRun.Start(trigger, Instance, full, changedSince, time.GetUtcNow());
        var run = started.Value;
        await runs.AddAsync(run, cancellationToken);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["InventoryRunId"] = run.Id,
            ["Trigger"] = trigger.ToString(),
        });

        LogStarted(full, changedSince);

        InventorySyncReport? report = null;
        try
        {
            report = await service.SyncAsync(new InventorySyncRequest(changedSince, full), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Fail(
                ResultError.Transient(InventorySyncErrors.Cancelled, "La sincronización se interrumpió porque el hub se estaba deteniendo."),
                default,
                time.GetUtcNow());
            await runs.SaveAsync(run, CancellationToken.None);
            LogCancelled();
            throw;
        }
        catch (Exception exception)
        {
            // La base de datos o algo inesperado: la ejecución queda registrada como fallida y el hub sigue vivo.
            run.Fail(ResultError.Unexpected(InventorySyncErrors.Unexpected, exception.Message), default, time.GetUtcNow());
            await runs.SaveAsync(run, cancellationToken);
            LogUnexpected(exception);
            return Result.Success(run);
        }

        if (report.Error is { } error)
        {
            run.Fail(error, report.Counts, time.GetUtcNow());
        }
        else
        {
            run.Complete(report.Counts, time.GetUtcNow());
        }

        await runs.SaveAsync(run, cancellationToken);
        LogFinished(run.Status, run.ItemsRead, run.ItemsUpdated, run.ItemsUnchanged, run.ItemsSkipped, run.ItemsFailed);
        if (run.ErrorCode is not null)
        {
            LogInterrupted(run.ErrorCode, run.ErrorMessage ?? string.Empty);
        }

        return Result.Success(run);
    }

    /// <summary>
    /// Desde dónde leer SAP: la fecha local de SAP en que empezó la última ejecución sin errores, retrocedida
    /// <see cref="InventorySyncOptions.OverlapDays"/> día. <c>UpdateDate</c> no trae hora ni zona horaria, así que el traslape
    /// absorbe las diferencias de reloj y de zona; la foto descarta lo que ya se envió. Sin ejecución previa, lee todo.
    /// </summary>
    private async Task<DateOnly?> ChangedSinceAsync(CancellationToken cancellationToken)
    {
        if (await runs.GetLastSucceededStartAsync(cancellationToken) is not { } lastStart)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(lastStart, _timeZone);
        return DateOnly.FromDateTime(local.DateTime).AddDays(-InventorySyncOptions.OverlapDays);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronización de stock y precios iniciada (completa: {Full}, desde: {ChangedSince})")]
    private partial void LogStarted(bool full, DateOnly? changedSince);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sincronización de stock y precios terminada ({Status}): {Read} leídos, {Updated} actualizados, {Unchanged} sin cambios, {Skipped} omitidos, {Failed} con error")]
    private partial void LogFinished(InventorySyncStatus status, int read, int updated, int unchanged, int skipped, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La sincronización de stock y precios se interrumpió ({ErrorCode}): {ErrorMessage}")]
    private partial void LogInterrupted(string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "No se inicia la sincronización de stock y precios ({Trigger}): ya hay otra en curso")]
    private partial void LogAlreadyRunning(InventorySyncTrigger trigger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} sincronizaciones de stock y precios habían quedado en curso (la instancia cayó); se marcaron como abandonadas")]
    private partial void LogAbandoned(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La sincronización de stock y precios se interrumpió porque el hub se está deteniendo")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "La sincronización de stock y precios falló por un error inesperado")]
    private partial void LogUnexpected(Exception exception);
}
