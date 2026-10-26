using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Application.Sync;

/// <summary>Códigos de los errores propios de la ejecución de un job.</summary>
public static class JobErrors
{
    /// <summary>El job superó su tiempo máximo de procesamiento.</summary>
    public const string Timeout = "job.timeout";

    /// <summary>Falló algo que no es un error de negocio de SAP: es un defecto o un problema local.</summary>
    public const string Unexpected = "job.unexpected";

    /// <summary>El job es de un origen o tipo que este servicio no sabe procesar.</summary>
    public const string Unsupported = "job.unsupported";

    /// <summary>
    /// El job ya acumuló más intentos de los permitidos sin llegar a cerrarse (el worker que lo tomaba cayó o excedió el lease
    /// una y otra vez): se manda al dead letter sin volver a procesarlo.
    /// </summary>
    public const string AttemptsExhausted = "job.attempts_exhausted";
}

/// <summary>
/// Ejecuta un job que un worker tiene tomado: lo sincroniza y lo cierra según el resultado. Si falla, <see cref="RetryPolicy"/>
/// decide (ADR 0010): los errores transitorios dejan el job esperando para reintentarlo con backoff exponencial; al agotar los
/// intentos pasa al dead letter; los errores definitivos lo dejan fallido sin reintentar.
/// </summary>
/// <remarks>
/// Tres garantías: (1) el job no puede seguir ejecutándose cuando su lease ya venció, porque tiene un tiempo máximo
/// menor que el lease; (2) cerrar el job usa un token propio, de modo que un apagado justo después de crear la orden
/// en SAP no pierda el resultado; (3) si el servicio se apaga a mitad de un job, el job se devuelve a la cola.
/// </remarks>
public sealed partial class SyncJobRunner(
    OrderSyncService orders,
    IJobQueue queue,
    RetryPolicy retry,
    ILogger<SyncJobRunner> logger)
{
    /// <summary>Tiempo que se concede a escribir el resultado en la base de datos, aunque el servicio se esté apagando.</summary>
    public static readonly TimeSpan ClosingTimeout = TimeSpan.FromSeconds(30);

    /// <param name="claim">El job tomado.</param>
    /// <param name="jobTimeout">Tiempo máximo para procesarlo; debe ser menor que el lease.</param>
    /// <param name="stoppingToken">Se cancela cuando el servicio se apaga.</param>
    public async Task RunAsync(ClaimedJob claim, TimeSpan jobTimeout, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = claim.JobId,
            ["Attempt"] = claim.AttemptNumber,
            ["Source"] = claim.Source,
            ["ExternalId"] = claim.ExternalId,
        });

        // Un job que ya agotó sus intentos sin cerrarse (un worker que cae una y otra vez) no se vuelve a procesar.
        if (claim.CycleAttempt > retry.MaxAttempts)
        {
            var exhausted = ResultError.Transient(
                JobErrors.AttemptsExhausted,
                $"El job acumuló {claim.CycleAttempt - 1} intentos sin terminar (máximo {retry.MaxAttempts}): el worker que lo tomaba cayó o excedió el tiempo del lease.");
            await CloseAsync(claim, null, exhausted, new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero));
            return;
        }

        LogStarted(claim.AttemptNumber, claim.Source, claim.ExternalId);

        OrderSyncOutcome? outcome = null;
        ResultError? failure = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(jobTimeout);

        try
        {
            if (claim.Source != SyncSources.WooCommerce || claim.Type != SyncJobTypes.Order)
            {
                failure = ResultError.Rejected(JobErrors.Unsupported, $"No se sabe procesar jobs de {claim.Source}/{claim.Type}.");
            }
            else
            {
                var result = await orders.SyncAsync(claim, timeout.Token);
                outcome = result.IsSuccess ? result.Value : null;
                failure = result.Error;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El servicio se apaga: el trabajo queda a medias, y como cada paso es idempotente, otro worker lo retoma.
            await ReleaseAsync(claim);
            return;
        }
        catch (OperationCanceledException)
        {
            failure = ResultError.Transient(JobErrors.Timeout, $"El procesamiento superó el tiempo máximo de {jobTimeout.TotalSeconds:0} segundos.");
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested)
        {
            // Con el servicio apagándose, un fallo distinto de la cancelación (un servicio ya desechado, una conexión cortada) es
            // consecuencia del apagado y no del job: no cuenta como error inesperado, el job vuelve a la cola.
            LogFailureWhileStopping(exception);
            await ReleaseAsync(claim);
            return;
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            failure = ResultError.Unexpected(JobErrors.Unexpected, $"Error inesperado ({exception.GetType().Name}): {exception.Message}");
        }

        await CloseAsync(claim, outcome, failure);
    }

    private async Task CloseAsync(ClaimedJob claim, OrderSyncOutcome? outcome, ResultError? failure, RetryDecision? forced = null)
    {
        // Token propio: el resultado de lo que ya se hizo en SAP no debe perderse porque el servicio esté apagándose.
        using var closing = new CancellationTokenSource(ClosingTimeout);

        Result closed;
        if (outcome is not null)
        {
            closed = await queue.CompleteAsync(claim, outcome.Order, closing.Token);
            if (closed.IsSuccess)
            {
                LogSucceeded(outcome.Order.DocEntry, outcome.Order.DocNum, outcome.AlreadyExisted);
            }
        }
        else
        {
            var decision = forced ?? retry.Decide(failure!, claim.CycleAttempt);
            closed = await queue.FailAsync(claim, failure!, decision, closing.Token);
            if (closed.IsSuccess)
            {
                LogFailure(claim, failure!, decision);
            }
        }

        if (closed.IsFailure)
        {
            LogNotClosed(closed.Error!.Code);
        }
    }

    private void LogFailure(ClaimedJob claim, ResultError failure, RetryDecision decision)
    {
        switch (decision.Action)
        {
            case RetryAction.RetryLater:
                LogRetrying(claim.CycleAttempt, retry.MaxAttempts, decision.Delay, failure.Kind, failure.Code, failure.Message);
                break;
            case RetryAction.DeadLetter:
                LogDeadLetter(claim.CycleAttempt, failure.Kind, failure.Code, failure.Message);
                break;
            default:
                LogFailed(failure.Kind, failure.Code, failure.Message);
                break;
        }
    }

    private async Task ReleaseAsync(ClaimedJob claim)
    {
        using var closing = new CancellationTokenSource(ClosingTimeout);
        var released = await queue.ReleaseAsync(claim, closing.Token);

        if (released.IsSuccess)
        {
            LogReleased();
        }
        else
        {
            LogNotClosed(released.Error!.Code);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Procesando el job (intento {Attempt}) de {Source} {ExternalId}")]
    private partial void LogStarted(int attempt, string source, string externalId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job completado: orden {DocEntry} (nº {DocNum}) en SAP; ya existía: {AlreadyExisted}")]
    private partial void LogSucceeded(int docEntry, int docNum, bool alreadyExisted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job fallido de forma definitiva ({Kind}, {Code}): {Message}. No se reintentará: requiere corregir el dato y reintentarlo desde el panel")]
    private partial void LogFailed(ErrorKind kind, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intento {Attempt} de {MaxAttempts} fallido ({Kind}, {Code}): {Message}. Se reintentará en {Delay}")]
    private partial void LogRetrying(int attempt, int maxAttempts, TimeSpan delay, ErrorKind kind, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "El job pasó al dead letter tras {Attempts} intentos ({Kind}, {Code}): {Message}")]
    private partial void LogDeadLetter(int attempts, ErrorKind kind, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error inesperado al procesar el job")]
    private partial void LogUnexpected(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del job mientras el servicio se apagaba; se devuelve a la cola")]
    private partial void LogFailureWhileStopping(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "El servicio se apaga: el job vuelve a la cola para que otro worker lo tome")]
    private partial void LogReleased();

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo cerrar el job ({Code}): lo retomó otro worker o ya estaba cerrado")]
    private partial void LogNotClosed(string code);
}
