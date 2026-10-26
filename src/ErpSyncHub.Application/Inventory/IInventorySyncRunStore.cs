using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.Application.Inventory;

/// <summary>El registro de las ejecuciones de la sincronización de stock y precios.</summary>
public interface IInventorySyncRunStore
{
    /// <summary>Guarda una ejecución recién iniciada.</summary>
    Task AddAsync(InventorySyncRun run, CancellationToken cancellationToken);

    /// <summary>Guarda los cambios de una ejecución que ya se había guardado (su cierre).</summary>
    Task SaveAsync(InventorySyncRun run, CancellationToken cancellationToken);

    /// <summary>La ejecución más reciente, o <see langword="null"/> si nunca hubo una.</summary>
    Task<InventorySyncRun?> GetLastAsync(CancellationToken cancellationToken);

    /// <summary>Las ejecuciones más recientes, de la última a la más antigua.</summary>
    Task<IReadOnlyList<InventorySyncRun>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>La ejecución más reciente que terminó sin errores, o <see langword="null"/> si ninguna.</summary>
    Task<InventorySyncRun?> GetLastSucceededAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cuándo empezó la última ejecución que terminó sin errores. Es el punto desde el que la siguiente puede leer solo lo
    /// que cambió: las que terminaron con errores o fallaron no avanzan el corte.
    /// </summary>
    Task<DateTimeOffset?> GetLastSucceededStartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cuándo empezó la última ejecución completa (<see cref="InventorySyncRun.Full"/>) que terminó sin errores, sea programada o
    /// manual; <see langword="null"/> si ninguna.
    /// </summary>
    Task<DateTimeOffset?> GetLastSucceededFullStartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cierra como abandonadas las ejecuciones que quedaron en curso. Solo es seguro llamarlo con la exclusión mutua
    /// tomada: ninguna otra instancia puede estar ejecutando una de verdad. Devuelve cuántas cerró.
    /// </summary>
    Task<int> AbandonRunningAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Garantiza que nunca corran dos sincronizaciones de stock y precios a la vez, ni siquiera en instancias distintas del hub.
/// </summary>
public interface IInventorySyncLock
{
    /// <summary>
    /// Intenta tomar la exclusión sin esperar. Devuelve <see langword="null"/> si otra ejecución la tiene; si no, un
    /// objeto que la libera al desecharse (o al caerse la conexión, si la instancia muere).
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}
