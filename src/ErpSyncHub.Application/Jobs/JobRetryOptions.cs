namespace ErpSyncHub.Application.Jobs;

/// <summary>
/// Cómo se reintentan automáticamente los jobs que fallaron por un error transitorio (sección <c>Worker:Retry</c>, variables
/// <c>Worker__Retry__*</c>). Los valores por defecto están pensados para sobrevivir a una caída o mantención de SAP de varias
/// horas: ver <see cref="RetryPolicy.TotalWindow"/> y el ADR 0010.
/// </summary>
public sealed class JobRetryOptions
{
    public const string SectionName = "Worker:Retry";

    /// <summary>Cota superior de intentos: más allá de esto un job que nunca se resuelve solo ocuparía la cola sin sentido.</summary>
    public const int MaxAllowedAttempts = 100;

    /// <summary>Cota inferior de la espera base. Existe para evitar un bucle de reintentos sin pausa, no por un caso de uso.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>Cota superior de cualquier espera: un día.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);

    /// <summary>
    /// Intentos totales de un job, contando el primero, antes de pasar al dead letter. Un reintento manual desde el panel
    /// concede un presupuesto nuevo. Por defecto, 12.
    /// </summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Espera tras el primer fallo; se duplica con cada intento. Por defecto, 30 segundos.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tope de la espera entre intentos. Por defecto, 1 hora.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Devuelve los motivos por los que la configuración no es válida; vacío si lo es.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxAttempts is < 1 or > MaxAllowedAttempts)
        {
            errors.Add($"Worker:Retry:MaxAttempts debe estar entre 1 y {MaxAllowedAttempts} (variable de entorno Worker__Retry__MaxAttempts).");
        }

        if (BaseDelay < MinimumDelay || BaseDelay > MaximumDelay)
        {
            errors.Add($"Worker:Retry:BaseDelay debe estar entre {MinimumDelay} y {MaximumDelay} (variable de entorno Worker__Retry__BaseDelay).");
        }

        if (MaxDelay < BaseDelay || MaxDelay > MaximumDelay)
        {
            errors.Add(
                $"Worker:Retry:MaxDelay debe ser al menos igual a BaseDelay y no superar {MaximumDelay} (variable de entorno Worker__Retry__MaxDelay).");
        }

        return errors;
    }
}
