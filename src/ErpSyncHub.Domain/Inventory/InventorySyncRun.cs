using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Domain.Inventory;

/// <summary>Qué disparó una ejecución de la sincronización de stock y precios.</summary>
public enum InventorySyncTrigger
{
    /// <summary>El intervalo configurado.</summary>
    Scheduled,

    /// <summary>Una persona, desde el panel.</summary>
    Manual,
}

/// <summary>Cómo terminó una ejecución de la sincronización de stock y precios.</summary>
public enum InventorySyncStatus
{
    /// <summary>En curso.</summary>
    Running,

    /// <summary>Terminó sin errores.</summary>
    Succeeded,

    /// <summary>Terminó, pero algunos artículos no se pudieron actualizar. Los reintenta la siguiente ejecución.</summary>
    CompletedWithErrors,

    /// <summary>Se interrumpió por un error general (SAP o la tienda inaccesibles, credenciales inválidas).</summary>
    Failed,

    /// <summary>La instancia que la ejecutaba cayó antes de cerrarla.</summary>
    Abandoned,
}

/// <summary>Contadores de una ejecución. Cada artículo leído de SAP cae en exactamente una de las cuatro últimas categorías.</summary>
/// <param name="Read">Artículos leídos de SAP.</param>
/// <param name="Updated">Artículos cuyo stock o precio se actualizó en la tienda.</param>
/// <param name="Unchanged">Artículos que no cambiaron respecto de la última foto confirmada: no se enviaron.</param>
/// <param name="Skipped">Artículos que no se pueden sincronizar por sus datos (sin producto en la tienda, sin precio válido).</param>
/// <param name="Failed">Artículos que la tienda rechazó o que no se pudieron enviar.</param>
public readonly record struct InventorySyncCounts(int Read, int Updated, int Unchanged, int Skipped, int Failed)
{
    /// <summary>Verdadero si ningún contador es negativo y la suma de las categorías coincide con lo leído.</summary>
    public bool IsConsistent =>
        Read >= 0 && Updated >= 0 && Unchanged >= 0 && Skipped >= 0 && Failed >= 0
        && Read == Updated + Unchanged + Skipped + Failed;

    internal bool IsValid => Read >= 0 && Updated >= 0 && Unchanged >= 0 && Skipped >= 0 && Failed >= 0;
}

/// <summary>Una ejecución de la sincronización de stock y precios: queda registrada con sus contadores y su resultado.</summary>
public sealed class InventorySyncRun
{
    public const int MaxInstanceLength = 100;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorMessageLength = 2000;

    // Para EF Core.
    private InventorySyncRun()
    {
    }

    public Guid Id { get; private set; }

    public InventorySyncTrigger Trigger { get; private set; }

    public InventorySyncStatus Status { get; private set; }

    /// <summary>Instancia del hub que la ejecuta (nombre de la máquina y proceso).</summary>
    public string Instance { get; private set; } = string.Empty;

    /// <summary>Lectura completa del maestro, ignorando la foto y la fecha de corte.</summary>
    public bool Full { get; private set; }

    /// <summary>Fecha de SAP desde la que se leyó; <see langword="null"/> si se leyó todo el maestro.</summary>
    public DateOnly? ChangedSince { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public int ItemsRead { get; private set; }

    public int ItemsUpdated { get; private set; }

    public int ItemsUnchanged { get; private set; }

    public int ItemsSkipped { get; private set; }

    public int ItemsFailed { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static Result<InventorySyncRun> Start(
        InventorySyncTrigger trigger,
        string instance,
        bool full,
        DateOnly? changedSince,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Length > MaxInstanceLength)
        {
            return Result.Failure<InventorySyncRun>(
                ResultError.Validation("inventory_run.invalid_instance", $"La instancia debe tener entre 1 y {MaxInstanceLength} caracteres."));
        }

        return Result.Success(new InventorySyncRun
        {
            Id = Guid.CreateVersion7(now),
            Trigger = trigger,
            Status = InventorySyncStatus.Running,
            Instance = instance,
            Full = full,
            ChangedSince = full ? null : changedSince,
            StartedAt = now,
        });
    }

    /// <summary>
    /// Cierra la ejecución: sin errores es <see cref="InventorySyncStatus.Succeeded"/>; con artículos fallidos,
    /// <see cref="InventorySyncStatus.CompletedWithErrors"/>.
    /// </summary>
    public Result Complete(InventorySyncCounts counts, DateTimeOffset now)
    {
        if (EnsureRunning() is { } notRunning)
        {
            return Result.Failure(notRunning);
        }

        if (!counts.IsConsistent)
        {
            return Result.Failure(ResultError.Validation(
                "inventory_run.inconsistent_counts",
                "Los contadores no suman lo leído: cada artículo debe estar en una sola categoría."));
        }

        Apply(counts);
        Status = counts.Failed == 0 ? InventorySyncStatus.Succeeded : InventorySyncStatus.CompletedWithErrors;
        FinishedAt = now;
        return Result.Success();
    }

    /// <summary>Cierra la ejecución por un error general, conservando lo que alcanzó a hacer.</summary>
    public Result Fail(ResultError error, InventorySyncCounts counts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (EnsureRunning() is { } notRunning)
        {
            return Result.Failure(notRunning);
        }

        if (!counts.IsValid)
        {
            return Result.Failure(ResultError.Validation("inventory_run.invalid_counts", "Los contadores no pueden ser negativos."));
        }

        Apply(counts);
        Status = InventorySyncStatus.Failed;
        ErrorCode = Truncate(error.Code, MaxErrorCodeLength);
        ErrorMessage = Truncate(error.Message, MaxErrorMessageLength);
        FinishedAt = now;
        return Result.Success();
    }

    private ResultError? EnsureRunning() =>
        Status == InventorySyncStatus.Running
            ? null
            : ResultError.Conflict("inventory_run.already_closed", $"La ejecución {Id} ya terminó: {Status}.");

    private void Apply(InventorySyncCounts counts)
    {
        ItemsRead = counts.Read;
        ItemsUpdated = counts.Updated;
        ItemsUnchanged = counts.Unchanged;
        ItemsSkipped = counts.Skipped;
        ItemsFailed = counts.Failed;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
