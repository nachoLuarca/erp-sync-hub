namespace ErpSyncHub.Domain.Resilience;

/// <summary>Estado de un circuit breaker.</summary>
public enum CircuitState
{
    /// <summary>Todo normal: el trabajo que depende del sistema externo se ejecuta.</summary>
    Closed,

    /// <summary>El sistema externo falla: no se le envía trabajo hasta la próxima prueba.</summary>
    Open,

    /// <summary>Pasó el enfriamiento y hay una prueba liviana en curso (una sola, en toda la flota).</summary>
    HalfOpen,
}

/// <summary>Por qué se abrió el circuito.</summary>
public enum CircuitOpenReason
{
    /// <summary>Fallas pasajeras sostenidas (sin conexión, errores 5xx, tiempos agotados).</summary>
    TransientFailures,

    /// <summary>SAP rechazó las credenciales: reintentar sin que alguien las corrija no sirve y puede bloquear al usuario.</summary>
    CredentialsRejected,
}

/// <summary>Cómo se abre y se enfría un circuito.</summary>
/// <param name="FailureThreshold">Fallas pasajeras consecutivas que lo abren.</param>
/// <param name="FailureWindow">Las fallas consecutivas deben caer dentro de este tiempo, contado desde la primera.</param>
/// <param name="CooldownBase">Enfriamiento tras abrirse por fallas pasajeras; se duplica con cada prueba fallida.</param>
/// <param name="CooldownMax">Tope del enfriamiento por fallas pasajeras.</param>
/// <param name="CredentialsCooldownBase">Enfriamiento tras abrirse por credenciales rechazadas.</param>
/// <param name="CredentialsCooldownMax">Tope del enfriamiento por credenciales rechazadas.</param>
/// <param name="ProbeLease">Cuánto vale la reserva de una prueba: si quien la hace cae, otra instancia la retoma al vencer.</param>
public sealed record CircuitSettings(
    int FailureThreshold,
    TimeSpan FailureWindow,
    TimeSpan CooldownBase,
    TimeSpan CooldownMax,
    TimeSpan CredentialsCooldownBase,
    TimeSpan CredentialsCooldownMax,
    TimeSpan ProbeLease);

/// <summary>Un cambio de estado del circuito, para registrarlo y mostrarlo.</summary>
/// <param name="From">Estado anterior.</param>
/// <param name="To">Estado nuevo.</param>
/// <param name="Reason">Motivo, cuando el nuevo estado es abierto.</param>
/// <param name="OpenCount">Veces seguidas que se abrió sin llegar a cerrarse (define el enfriamiento).</param>
/// <param name="NextProbeAt">Cuándo toca la próxima prueba, cuando el nuevo estado es abierto.</param>
public sealed record CircuitTransition(CircuitState From, CircuitState To, CircuitOpenReason? Reason, int OpenCount, DateTimeOffset? NextProbeAt);

/// <summary>
/// El estado de un circuit breaker (ADR 0011). Se guarda en PostgreSQL para que lo compartan todas las instancias del hub; la fila
/// se modifica bajo bloqueo, así que dos instancias nunca deciden a la vez. Cerrado deja pasar el trabajo; abierto lo detiene; semiabierto
/// permite una única prueba, y si pasa se cierra.
/// </summary>
public sealed class CircuitBreaker
{
    public const int MaxNameLength = 50;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorMessageLength = 2000;

    private const int MaxCooldownExponent = 30;

    // Para EF Core.
    private CircuitBreaker()
    {
    }

    /// <summary>Nombre del sistema que protege, por ejemplo <c>sap</c>.</summary>
    public string Name { get; private set; } = string.Empty;

    public CircuitState State { get; private set; }

    /// <summary>Fallas pasajeras consecutivas con el circuito cerrado; cualquier éxito la reinicia.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Cuándo ocurrió la primera falla de la racha actual.</summary>
    public DateTimeOffset? FirstFailureAt { get; private set; }

    public CircuitOpenReason? Reason { get; private set; }

    public DateTimeOffset? OpenedAt { get; private set; }

    /// <summary>Veces seguidas que se abrió sin cerrarse: 1 la primera vez, y una más por cada prueba fallida.</summary>
    public int OpenCount { get; private set; }

    /// <summary>Cuándo se puede hacer la próxima prueba.</summary>
    public DateTimeOffset? NextProbeAt { get; private set; }

    /// <summary>Hasta cuándo es válida la reserva de la prueba en curso.</summary>
    public DateTimeOffset? ProbeLeaseUntil { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Crea un circuito cerrado.</summary>
    public static CircuitBreaker Create(string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, MaxNameLength);

        return new CircuitBreaker { Name = name, State = CircuitState.Closed, UpdatedAt = now };
    }

    /// <summary>Verdadero si el trabajo que depende del sistema puede ejecutarse.</summary>
    public bool AllowsWork => State == CircuitState.Closed;

    /// <summary>Verdadero si ya toca una prueba: pasó el enfriamiento, o la reserva de la prueba anterior venció sin resultado.</summary>
    public bool IsProbeDue(DateTimeOffset now) =>
        (State == CircuitState.Open && NextProbeAt <= now)
        || (State == CircuitState.HalfOpen && ProbeLeaseUntil <= now);

    /// <summary>
    /// Un éxito con el circuito cerrado reinicia la racha de fallas. Con el circuito abierto o semiabierto no cambia nada: solo la
    /// prueba lo cierra, para que un trabajo que alcanzó a terminar no lo cierre por casualidad.
    /// </summary>
    public void RecordSuccess(DateTimeOffset now)
    {
        if (State != CircuitState.Closed || ConsecutiveFailures == 0)
        {
            return;
        }

        ConsecutiveFailures = 0;
        FirstFailureAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Una falla pasajera. Con el circuito cerrado cuenta para la racha: si las fallas consecutivas llegan al umbral dentro de la
    /// ventana (contada desde la primera), el circuito se abre. Una falla fuera de la ventana empieza una racha nueva.
    /// </summary>
    public CircuitTransition? RecordTransientFailure(string errorCode, string? errorMessage, DateTimeOffset now, CircuitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (State != CircuitState.Closed)
        {
            return null; // ya está abierto: la falla de un trabajo que venía en vuelo no cambia nada
        }

        if (FirstFailureAt is null || now - FirstFailureAt > settings.FailureWindow)
        {
            ConsecutiveFailures = 1;
            FirstFailureAt = now;
        }
        else
        {
            ConsecutiveFailures++;
        }

        Remember(errorCode, errorMessage, now);

        return ConsecutiveFailures >= settings.FailureThreshold
            ? Open(CircuitOpenReason.TransientFailures, openCount: 1, now, settings)
            : null;
    }

    /// <summary>SAP rechazó las credenciales: se abre de inmediato, sin esperar a que se acumulen fallas.</summary>
    public CircuitTransition? RecordCredentialsRejected(string errorCode, string? errorMessage, DateTimeOffset now, CircuitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (State != CircuitState.Closed)
        {
            return null;
        }

        ConsecutiveFailures++;
        FirstFailureAt ??= now;
        Remember(errorCode, errorMessage, now);
        return Open(CircuitOpenReason.CredentialsRejected, openCount: 1, now, settings);
    }

    /// <summary>
    /// Reserva la prueba si ya toca (ver <see cref="IsProbeDue"/>): pasa a semiabierto. Devuelve <see langword="null"/> si no toca o si
    /// otra instancia ya la tiene reservada, de modo que solo una prueba corre a la vez.
    /// </summary>
    public CircuitTransition? TryBeginProbe(DateTimeOffset now, CircuitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsProbeDue(now))
        {
            return null;
        }

        var from = State;
        State = CircuitState.HalfOpen;
        ProbeLeaseUntil = now + settings.ProbeLease;
        UpdatedAt = now;
        return new CircuitTransition(from, State, Reason, OpenCount, NextProbeAt);
    }

    /// <summary>La prueba pasó: el circuito se cierra y la cola se reanuda.</summary>
    public CircuitTransition? CompleteProbeSuccess(DateTimeOffset now)
    {
        if (State != CircuitState.HalfOpen)
        {
            return null;
        }

        return Close(now);
    }

    /// <summary>La prueba falló: vuelve a abrirse con un enfriamiento más largo (crece al doble, hasta el tope).</summary>
    public CircuitTransition? CompleteProbeFailure(
        bool credentialsRejected,
        string errorCode,
        string? errorMessage,
        DateTimeOffset now,
        CircuitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (State != CircuitState.HalfOpen)
        {
            return null;
        }

        Remember(errorCode, errorMessage, now);
        return Open(
            credentialsRejected ? CircuitOpenReason.CredentialsRejected : CircuitOpenReason.TransientFailures,
            OpenCount + 1,
            now,
            settings);
    }

    /// <summary>Lo cierra una persona, sin esperar la prueba (por ejemplo, tras corregir las credenciales).</summary>
    public CircuitTransition? Close(DateTimeOffset now)
    {
        if (State == CircuitState.Closed)
        {
            return null;
        }

        var from = State;
        State = CircuitState.Closed;
        ConsecutiveFailures = 0;
        FirstFailureAt = null;
        Reason = null;
        OpenedAt = null;
        OpenCount = 0;
        NextProbeAt = null;
        ProbeLeaseUntil = null;
        UpdatedAt = now;
        return new CircuitTransition(from, State, Reason: null, OpenCount: 0, NextProbeAt: null);
    }

    /// <summary>El enfriamiento tras abrirse por <paramref name="openCount"/>-ésima vez: la base, el doble, el cuádruple... hasta el tope.</summary>
    public static TimeSpan Cooldown(CircuitOpenReason reason, int openCount, CircuitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(openCount, 1);

        var (baseDelay, maxDelay) = reason == CircuitOpenReason.CredentialsRejected
            ? (settings.CredentialsCooldownBase, settings.CredentialsCooldownMax)
            : (settings.CooldownBase, settings.CooldownMax);

        var factor = Math.Pow(2, Math.Min(openCount - 1, MaxCooldownExponent));
        var ticks = Math.Min(baseDelay.Ticks * factor, maxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private CircuitTransition Open(CircuitOpenReason reason, int openCount, DateTimeOffset now, CircuitSettings settings)
    {
        var from = State;
        State = CircuitState.Open;
        Reason = reason;
        OpenedAt = now;
        OpenCount = openCount;
        NextProbeAt = now + Cooldown(reason, openCount, settings);
        ProbeLeaseUntil = null;
        UpdatedAt = now;
        return new CircuitTransition(from, State, Reason, OpenCount, NextProbeAt);
    }

    private void Remember(string errorCode, string? errorMessage, DateTimeOffset now)
    {
        LastErrorCode = Truncate(errorCode, MaxErrorCodeLength);
        LastErrorMessage = errorMessage is null ? null : Truncate(errorMessage, MaxErrorMessageLength);
        UpdatedAt = now;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
