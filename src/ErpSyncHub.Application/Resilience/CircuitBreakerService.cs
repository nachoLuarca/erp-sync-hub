using ErpSyncHub.Domain.Resilience;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.Application.Resilience;

/// <summary>El resultado de la prueba liviana contra el sistema externo.</summary>
/// <param name="Succeeded">Verdadero si el sistema respondió bien (inició sesión y leyó un dato).</param>
/// <param name="CredentialsRejected">Verdadero si falló porque rechazó las credenciales.</param>
/// <param name="ErrorCode">Código del error, si falló.</param>
/// <param name="ErrorMessage">Mensaje del error (ya sin datos personales), si falló.</param>
public sealed record CircuitProbeResult(bool Succeeded, bool CredentialsRejected, string? ErrorCode, string? ErrorMessage)
{
    public static CircuitProbeResult Success { get; } = new(true, false, null, null);

    public static CircuitProbeResult Failure(string code, string? message, bool credentialsRejected = false) =>
        new(false, credentialsRejected, code, message);
}

/// <summary>
/// Las reglas del circuit breaker con su persistencia y su registro (ADR 0011): informar éxitos y fallas, hacer la prueba
/// semiabierta cuando toca y cerrarlo a mano. Cada cambio de estado deja una línea de log, visible en Seq.
/// </summary>
public sealed partial class CircuitBreakerService(
    ICircuitBreakerStore store,
    CircuitBreakerOptions options,
    TimeProvider time,
    ILogger<CircuitBreakerService> logger)
{
    /// <summary>Falso si el circuito está deshabilitado por configuración: nunca se abre.</summary>
    public bool Enabled => options.Enabled;

    public Task<CircuitSnapshot> GetAsync(string name, CancellationToken cancellationToken) => store.GetAsync(name, cancellationToken);

    public Task<IReadOnlyList<CircuitSnapshot>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);

    /// <summary>Verdadero si el trabajo que depende del sistema puede ejecutarse (circuito cerrado, o deshabilitado).</summary>
    public async Task<bool> AllowsWorkAsync(string name, CancellationToken cancellationToken) =>
        !options.Enabled || (await store.GetAsync(name, cancellationToken)).AllowsWork;

    /// <summary>
    /// El sistema respondió con sentido (aunque haya sido un rechazo de negocio): reinicia la racha de fallas. Solo escribe si hay una
    /// racha que reiniciar; <paramref name="known"/> evita volver a leer el estado cuando quien llama ya lo tiene.
    /// </summary>
    public async Task ReportSuccessAsync(string name, CircuitSnapshot? known, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        known ??= await store.GetAsync(name, cancellationToken);
        if (known.State != CircuitState.Closed || known.ConsecutiveFailures == 0)
        {
            return;
        }

        await store.UpdateAsync(
            name,
            circuit =>
            {
                circuit.RecordSuccess(time.GetUtcNow());
                return true;
            },
            cancellationToken);
    }

    /// <summary>Una falla pasajera del sistema (sin conexión, error del servidor, tiempo agotado): cuenta para abrir el circuito.</summary>
    public async Task ReportTransientFailureAsync(string name, string errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var transition = await store.UpdateAsync(
            name,
            circuit => circuit.RecordTransientFailure(errorCode, errorMessage, time.GetUtcNow(), options.ToSettings()),
            cancellationToken);
        Log(name, transition, errorCode);
    }

    /// <summary>El sistema rechazó las credenciales: el circuito se abre de inmediato.</summary>
    public async Task ReportCredentialsRejectedAsync(string name, string errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var transition = await store.UpdateAsync(
            name,
            circuit => circuit.RecordCredentialsRejected(errorCode, errorMessage, time.GetUtcNow(), options.ToSettings()),
            cancellationToken);
        Log(name, transition, errorCode);
    }

    /// <summary>
    /// Si toca una prueba (pasó el enfriamiento, o la reserva de la anterior venció), la reserva —una sola en toda la flota—, la
    /// ejecuta y cierra el circuito si pasa o lo vuelve a abrir con más enfriamiento si falla. Devuelve verdadero si esta
    /// instancia hizo la prueba.
    /// </summary>
    /// <param name="name">El circuito.</param>
    /// <param name="probe">La prueba liviana: iniciar sesión y leer un dato del sistema.</param>
    /// <param name="cancellationToken">Cancela la prueba (por ejemplo, al apagarse el hub); la reserva vence sola y otra instancia la retoma.</param>
    public async Task<bool> RunProbeIfDueAsync(
        string name,
        Func<CancellationToken, Task<CircuitProbeResult>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!options.Enabled)
        {
            return false;
        }

        var current = await store.GetAsync(name, cancellationToken);
        if (!current.IsProbeDue(time.GetUtcNow()))
        {
            return false;
        }

        // La reserva es atómica: si otra instancia llegó primero, esta recibe null y no hace nada.
        var began = await store.UpdateAsync(name, circuit => circuit.TryBeginProbe(time.GetUtcNow(), options.ToSettings()), cancellationToken);
        if (began is null)
        {
            return false;
        }

        Log(name, began, errorCode: null);

        CircuitProbeResult result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ProbeLease);
        try
        {
            result = await probe(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = CircuitProbeResult.Failure("circuit.probe_timeout", "La prueba no terminó dentro de la reserva.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // La prueba nunca debe romper al worker: un fallo inesperado cuenta como prueba fallida.
            LogProbeCrashed(exception, name);
            result = CircuitProbeResult.Failure("circuit.probe_error", exception.GetType().Name);
        }

        // Guardar el resultado no se cancela con el apagado: la prueba ya se hizo.
        var completed = await store.UpdateAsync(
            name,
            circuit => result.Succeeded
                ? circuit.CompleteProbeSuccess(time.GetUtcNow())
                : circuit.CompleteProbeFailure(
                    result.CredentialsRejected,
                    result.ErrorCode ?? "circuit.probe_failed",
                    result.ErrorMessage,
                    time.GetUtcNow(),
                    options.ToSettings()),
            CancellationToken.None);
        Log(name, completed, result.ErrorCode);
        return true;
    }

    /// <summary>Lo cierra una persona, sin esperar la prueba. Devuelve el estado resultante.</summary>
    public async Task<CircuitSnapshot> CloseAsync(string name, CancellationToken cancellationToken)
    {
        var transition = await store.UpdateAsync(name, circuit => circuit.Close(time.GetUtcNow()), cancellationToken);
        Log(name, transition, errorCode: "manual");
        return await store.GetAsync(name, cancellationToken);
    }

    private void Log(string name, CircuitTransition? transition, string? errorCode)
    {
        if (transition is null)
        {
            return;
        }

        switch (transition.To)
        {
            case CircuitState.Open:
                LogOpened(name, transition.From, transition.To, transition.Reason, transition.OpenCount, transition.NextProbeAt, errorCode);
                break;
            case CircuitState.HalfOpen:
                LogHalfOpen(name, transition.From, transition.To, transition.OpenCount);
                break;
            default:
                LogClosed(name, transition.From, transition.To, errorCode);
                break;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Circuito {CircuitName} ABIERTO ({Reason}, apertura n.º {OpenCount}): no se enviará trabajo hasta la prueba de {NextProbeAt:O}. " +
                  "Estado {FromState} → {ToState}, último error {ErrorCode}")]
    private partial void LogOpened(
        string circuitName,
        CircuitState fromState,
        CircuitState toState,
        CircuitOpenReason? reason,
        int openCount,
        DateTimeOffset? nextProbeAt,
        string? errorCode);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Circuito {CircuitName} semiabierto: se hace una prueba liviana (apertura n.º {OpenCount}). Estado {FromState} → {ToState}")]
    private partial void LogHalfOpen(string circuitName, CircuitState fromState, CircuitState toState, int openCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Circuito {CircuitName} CERRADO: la cola se reanuda. Estado {FromState} → {ToState} ({ErrorCode})")]
    private partial void LogClosed(string circuitName, CircuitState fromState, CircuitState toState, string? errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "La prueba del circuito {CircuitName} falló con un error inesperado")]
    private partial void LogProbeCrashed(Exception exception, string circuitName);
}
