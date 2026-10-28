using ErpSyncHub.Domain.Resilience;

namespace ErpSyncHub.Application.Resilience;

/// <summary>Nombres de los circuitos del hub.</summary>
public static class CircuitNames
{
    /// <summary>SAP Business One.</summary>
    public const string Sap = "sap";
}

/// <summary>Códigos de error del circuit breaker.</summary>
public static class CircuitErrors
{
    /// <summary>El circuito está abierto: no se llamó al sistema externo. Es pasajero y no gasta un intento del job.</summary>
    public const string Open = "circuit.open";
}

/// <summary>Una copia del estado de un circuito, para consultarlo sin bloquear nada.</summary>
public sealed record CircuitSnapshot(
    string Name,
    CircuitState State,
    int ConsecutiveFailures,
    CircuitOpenReason? Reason,
    DateTimeOffset? OpenedAt,
    int OpenCount,
    DateTimeOffset? NextProbeAt,
    DateTimeOffset? ProbeLeaseUntil,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Verdadero si el trabajo que depende del sistema puede ejecutarse.</summary>
    public bool AllowsWork => State == CircuitState.Closed;

    /// <summary>Verdadero si ya toca una prueba: pasó el enfriamiento, o la reserva de la prueba anterior venció sin resultado.</summary>
    public bool IsProbeDue(DateTimeOffset now) =>
        (State == CircuitState.Open && NextProbeAt <= now)
        || (State == CircuitState.HalfOpen && ProbeLeaseUntil <= now);

    public static CircuitSnapshot From(CircuitBreaker circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        return new CircuitSnapshot(
            circuit.Name,
            circuit.State,
            circuit.ConsecutiveFailures,
            circuit.Reason,
            circuit.OpenedAt,
            circuit.OpenCount,
            circuit.NextProbeAt,
            circuit.ProbeLeaseUntil,
            circuit.LastErrorCode,
            circuit.LastErrorMessage,
            circuit.UpdatedAt);
    }
}

/// <summary>
/// Dónde viven los circuitos: en PostgreSQL, compartidos por todas las instancias del hub. Un cambio se hace bajo bloqueo de la
/// fila, así que dos instancias nunca deciden a la vez.
/// </summary>
public interface ICircuitBreakerStore
{
    /// <summary>El estado actual del circuito; si no existe todavía, uno cerrado.</summary>
    Task<CircuitSnapshot> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Todos los circuitos.</summary>
    Task<IReadOnlyList<CircuitSnapshot>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Aplica <paramref name="change"/> al circuito bajo bloqueo de su fila y guarda el resultado. Devuelve lo que devolvió
    /// <paramref name="change"/> (normalmente la transición, si hubo una).
    /// </summary>
    Task<TResult> UpdateAsync<TResult>(string name, Func<CircuitBreaker, TResult> change, CancellationToken cancellationToken);
}
