namespace ErpSyncHub.Domain.Jobs;

/// <summary>Estado de un <see cref="SyncJob"/>.</summary>
public enum JobStatus
{
    /// <summary>Esperando que un worker lo tome.</summary>
    Pending,

    /// <summary>Tomado por un worker, mientras dure su lease.</summary>
    Processing,

    /// <summary>La orden quedó creada en SAP.</summary>
    Succeeded,

    /// <summary>
    /// Falló por un error transitorio y espera su próximo intento (<see cref="SyncJob.NextAttemptAt"/>), con espera exponencial.
    /// </summary>
    Retrying,

    /// <summary>
    /// Falló por un error definitivo (SKU sin mapeo, RUT inválido…): reintentar no cambiaría nada. Terminal; requiere que una
    /// persona corrija el dato y lo reintente a mano.
    /// </summary>
    Failed,

    /// <summary>
    /// Falló por errores transitorios y se agotaron los intentos automáticos. Terminal; queda a la vista en el panel para
    /// revisarlo y reintentarlo a mano.
    /// </summary>
    DeadLetter,
}

/// <summary>Resultado de un <see cref="SyncAttempt"/>.</summary>
public enum AttemptOutcome
{
    /// <summary>El worker lo está ejecutando.</summary>
    InProgress,

    Succeeded,

    Failed,

    /// <summary>El worker dejó de renovar el lease (cayó o se apagó) y otro retomó el job.</summary>
    Abandoned,
}

/// <summary>Valores de <see cref="SyncJob.Source"/>.</summary>
public static class SyncSources
{
    public const string WooCommerce = "woocommerce";
}

/// <summary>Valores de <see cref="SyncJob.Type"/>.</summary>
public static class SyncJobTypes
{
    /// <summary>Pedido de la tienda que se convierte en una orden de venta en SAP.</summary>
    public const string Order = "order";
}
