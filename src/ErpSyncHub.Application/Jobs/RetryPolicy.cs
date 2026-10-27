using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Application.Jobs;

/// <summary>Qué hacer con un job que acaba de fallar.</summary>
public enum RetryAction
{
    /// <summary>Volver a intentarlo tras una espera.</summary>
    RetryLater,

    /// <summary>El error es transitorio pero se agotaron los intentos: queda en el dead letter para revisión.</summary>
    DeadLetter,

    /// <summary>El error es definitivo: reintentar no cambiaría nada. Queda fallido hasta que una persona lo corrija.</summary>
    Fail,
}

/// <param name="Action">Qué hacer con el job.</param>
/// <param name="Delay">Cuánto esperar antes del próximo intento; cero salvo en <see cref="RetryAction.RetryLater"/>.</param>
public readonly record struct RetryDecision(RetryAction Action, TimeSpan Delay);

/// <summary>
/// Decide si un job fallido se reintenta y cuándo (ADR 0010). Solo se reintentan los errores que pueden resolverse solos:
/// los <see cref="ErrorKind.Transient"/> (SAP caído, tiempo agotado, 5xx) y los <see cref="ErrorKind.Unexpected"/>
/// (un defecto o un problema local que podría ser pasajero; el worker los registra con su excepción). Los demás
/// (SKU sin mapeo, RUT inválido, cliente inhabilitado…) son definitivos.
/// </summary>
/// <remarks>
/// La espera crece exponencialmente (<see cref="JobRetryOptions.BaseDelay"/> × 2<sup>n-1</sup>, hasta
/// <see cref="JobRetryOptions.MaxDelay"/>) y se aleja entre el 50 % y el 100 % de ese valor al azar (<i>jitter</i>), para que
/// los jobs que fallaron juntos por una misma caída no vuelvan a golpear a SAP todos a la vez cuando se recupere.
/// </remarks>
/// <param name="options">Configuración de los reintentos.</param>
/// <param name="nextRandom">Fuente de azar en [0, 1); inyectable para que las pruebas sean deterministas.</param>
public sealed class RetryPolicy(JobRetryOptions options, Func<double> nextRandom)
{
    /// <summary>Fracción mínima de la espera que conserva el jitter: la espera real está entre este valor y 1.</summary>
    public const double JitterFloor = 0.5;

    /// <summary>Intentos totales de un ciclo antes de pasar al dead letter.</summary>
    public int MaxAttempts => options.MaxAttempts;

    /// <summary>Decide qué hacer tras el fallo de un intento.</summary>
    /// <param name="error">Por qué falló.</param>
    /// <param name="attemptsMade">Intentos hechos en el ciclo actual, contando el que acaba de fallar (desde 1).</param>
    public RetryDecision Decide(ResultError error, int attemptsMade)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);

        if (!IsRetryable(error))
        {
            return new RetryDecision(RetryAction.Fail, TimeSpan.Zero);
        }

        return attemptsMade >= options.MaxAttempts
            ? new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero)
            : new RetryDecision(RetryAction.RetryLater, DelayAfter(attemptsMade));
    }

    /// <summary>Verdadero si el error puede resolverse solo y, por tanto, vale la pena reintentar.</summary>
    public static bool IsRetryable(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind is ErrorKind.Transient or ErrorKind.Unexpected;
    }

    /// <summary>La espera, con jitter, antes del intento siguiente a <paramref name="attemptsMade"/> intentos fallidos.</summary>
    public TimeSpan DelayAfter(int attemptsMade)
    {
        var jitter = JitterFloor + ((1 - JitterFloor) * Math.Clamp(nextRandom(), 0, 1));
        return TimeSpan.FromTicks((long)(CeilingDelayAfter(attemptsMade).Ticks * jitter));
    }

    /// <summary>
    /// El tiempo que transcurre, sin contar lo que dure cada intento, desde el primer fallo hasta que el job pasa al dead
    /// letter: la ventana de caída de SAP que el job puede atravesar. El mínimo y el máximo son los extremos del jitter.
    /// </summary>
    public (TimeSpan Min, TimeSpan Max) TotalWindow()
    {
        var max = TimeSpan.Zero;
        for (var attempt = 1; attempt < options.MaxAttempts; attempt++)
        {
            max += CeilingDelayAfter(attempt);
        }

        return (TimeSpan.FromTicks((long)(max.Ticks * JitterFloor)), max);
    }

    /// <summary>La espera sin jitter: el techo de lo que puede durar.</summary>
    private TimeSpan CeilingDelayAfter(int attemptsMade)
    {
        // Se calcula en doble precisión y se acota antes de convertir, para que un exponente grande no desborde.
        var ticks = options.BaseDelay.Ticks * Math.Pow(2, Math.Min(attemptsMade - 1, 62));
        return TimeSpan.FromTicks((long)Math.Min(ticks, options.MaxDelay.Ticks));
    }
}
