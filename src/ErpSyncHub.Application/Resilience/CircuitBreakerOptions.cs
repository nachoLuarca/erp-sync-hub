using ErpSyncHub.Domain.Resilience;

namespace ErpSyncHub.Application.Resilience;

/// <summary>
/// Cuándo se abre el circuit breaker de SAP y cómo se enfría (sección <c>Circuit</c>, variables <c>Circuit__*</c>; en Compose
/// <c>ERPSYNC_CIRCUIT_*</c>). Los valores por defecto están en el ADR 0011.
/// </summary>
public sealed class CircuitBreakerOptions
{
    public const string SectionName = "Circuit";

    /// <summary>Cota superior de cualquier espera: un día.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);

    /// <summary>Con falso el circuito nunca se abre y el hub se comporta como antes (solo los reintentos con backoff). Por defecto, verdadero.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fallas pasajeras consecutivas que lo abren. Por defecto, 5.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Las fallas consecutivas deben caer dentro de este tiempo, contado desde la primera. Con poco tráfico y backoff, una ventana
    /// corta podría no juntar nunca las fallas: por defecto, 5 minutos.
    /// </summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Enfriamiento antes de la primera prueba; se duplica con cada prueba fallida. Por defecto, 30 segundos.</summary>
    public TimeSpan CooldownBase { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tope del enfriamiento. Por defecto, 15 minutos.</summary>
    public TimeSpan CooldownMax { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Enfriamiento cuando SAP rechazó las credenciales. Probar seguido con una contraseña mala puede bloquear al usuario de SAP: por
    /// defecto, 15 minutos.
    /// </summary>
    public TimeSpan CredentialsCooldownBase { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Tope del enfriamiento por credenciales. Por defecto, 1 hora.</summary>
    public TimeSpan CredentialsCooldownMax { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Cuánto vale la reserva de una prueba: si la instancia que la hace cae, otra la retoma al vencer. Por defecto, 30 segundos.</summary>
    public TimeSpan ProbeLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>La configuración en la forma que usa el dominio.</summary>
    public CircuitSettings ToSettings() =>
        new(FailureThreshold, FailureWindow, CooldownBase, CooldownMax, CredentialsCooldownBase, CredentialsCooldownMax, ProbeLease);

    /// <summary>Devuelve los motivos por los que la configuración no es válida; vacío si lo es.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (FailureThreshold is < 1 or > 100)
        {
            errors.Add("Circuit:FailureThreshold debe estar entre 1 y 100 (variable de entorno Circuit__FailureThreshold, en Compose ERPSYNC_CIRCUIT_FAILURE_THRESHOLD).");
        }

        if (FailureWindow < TimeSpan.FromSeconds(1) || FailureWindow > MaximumDelay)
        {
            errors.Add($"Circuit:FailureWindow debe estar entre 00:00:01 y {MaximumDelay} (variable de entorno Circuit__FailureWindow, en Compose ERPSYNC_CIRCUIT_FAILURE_WINDOW).");
        }

        CheckCooldown(errors, "CooldownBase", "CooldownMax", CooldownBase, CooldownMax, "ERPSYNC_CIRCUIT_COOLDOWN_BASE", "ERPSYNC_CIRCUIT_COOLDOWN_MAX");
        CheckCooldown(
            errors,
            "CredentialsCooldownBase",
            "CredentialsCooldownMax",
            CredentialsCooldownBase,
            CredentialsCooldownMax,
            "ERPSYNC_CIRCUIT_CREDENTIALS_COOLDOWN_BASE",
            "ERPSYNC_CIRCUIT_CREDENTIALS_COOLDOWN_MAX");

        if (ProbeLease < TimeSpan.FromSeconds(5) || ProbeLease > TimeSpan.FromMinutes(10))
        {
            errors.Add("Circuit:ProbeLease debe estar entre 00:00:05 y 00:10:00 (variable de entorno Circuit__ProbeLease, en Compose ERPSYNC_CIRCUIT_PROBE_LEASE).");
        }

        return errors;
    }

    private static void CheckCooldown(
        List<string> errors,
        string baseName,
        string maxName,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        string baseVariable,
        string maxVariable)
    {
        if (baseDelay < TimeSpan.FromSeconds(1) || baseDelay > MaximumDelay)
        {
            errors.Add($"Circuit:{baseName} debe estar entre 00:00:01 y {MaximumDelay} (variable de entorno Circuit__{baseName}, en Compose {baseVariable}).");
        }

        if (maxDelay < baseDelay || maxDelay > MaximumDelay)
        {
            errors.Add($"Circuit:{maxName} debe ser al menos igual a {baseName} y no superar {MaximumDelay} (variable de entorno Circuit__{maxName}, en Compose {maxVariable}).");
        }
    }
}
