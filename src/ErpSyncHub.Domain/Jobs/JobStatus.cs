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

    /// <summary>El intento falló. En esta fase no se reintenta solo: backoff y dead letter llegan después.</summary>
    Failed,
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
