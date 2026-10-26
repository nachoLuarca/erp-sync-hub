using System.Text.Json.Serialization;

namespace SapB1.Mock.Faults;

/// <summary>Cómo se manifiesta una caída total del Service Layer.</summary>
internal enum OutageMode
{
    /// <summary>Responde HTTP 503: el servicio está detrás de un balanceador que informa que no hay nodos disponibles.</summary>
    Unavailable,

    /// <summary>Corta la conexión sin responder: el proceso del Service Layer cayó.</summary>
    ConnectionReset,
}

/// <summary>
/// Fallas que el mock inyecta en el Service Layer (<c>/b1s/v1</c>) para probar la resiliencia del cliente. Se activan con
/// <c>PUT /_mock/faults</c> y se combinan: la latencia afecta a todas las peticiones y la caída total prevalece sobre los
/// errores intermitentes. Los endpoints de administración (<c>/_mock</c>) y <c>/health</c> nunca se ven afectados.
/// </summary>
/// <param name="Outage">Caída total: ninguna petición se atiende, ni siquiera el login.</param>
/// <param name="OutageMode">Cómo se manifiesta la caída total.</param>
/// <param name="LatencyMs">Demora, en milisegundos, antes de atender cada petición.</param>
/// <param name="ErrorEveryNth">Cada N-ésima petición responde con <paramref name="ErrorStatusCode"/> (0 la desactiva). Es determinista.</param>
/// <param name="ErrorRate">Probabilidad (0 a 1) de que cada petición responda con <paramref name="ErrorStatusCode"/>.</param>
/// <param name="ErrorStatusCode">Código HTTP 5xx de los errores intermitentes.</param>
/// <param name="SessionExpiryEveryNth">Cada N-ésima petición autenticada encuentra su sesión expirada (HTTP 401, código 301) y la pierde (0 la desactiva).</param>
internal sealed record FaultSettings(
    bool Outage = false,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OutageMode OutageMode = OutageMode.Unavailable,
    int LatencyMs = 0,
    int ErrorEveryNth = 0,
    double ErrorRate = 0,
    int ErrorStatusCode = 503,
    int SessionExpiryEveryNth = 0)
{
    public const int MaxLatencyMs = 120_000;

    /// <summary>Verdadero si no hay ninguna falla activa.</summary>
    [JsonIgnore]
    public bool IsEmpty => this == new FaultSettings();

    /// <summary>Devuelve el motivo por el que la configuración no es válida, o <see langword="null"/> si lo es.</summary>
    public string? Validate() =>
        !Enum.IsDefined(OutageMode) ? "OutageMode debe ser Unavailable o ConnectionReset."
        : LatencyMs is < 0 or > MaxLatencyMs ? $"LatencyMs debe estar entre 0 y {MaxLatencyMs}."
        : ErrorEveryNth < 0 ? "ErrorEveryNth no puede ser negativo."
        : double.IsNaN(ErrorRate) || ErrorRate is < 0 or > 1 ? "ErrorRate debe estar entre 0 y 1."
        : ErrorStatusCode is < 500 or > 599 ? "ErrorStatusCode debe ser un código 5xx."
        : SessionExpiryEveryNth < 0 ? "SessionExpiryEveryNth no puede ser negativo."
        : null;
}

/// <summary>Cuántas veces se inyectó cada falla desde que se activó la configuración actual.</summary>
internal sealed record FaultCounters(long Delayed, long Outages, long Errors, long SessionExpiries);

/// <summary>Lo que devuelve <c>GET /_mock/faults</c>.</summary>
internal sealed record FaultReport(FaultSettings Settings, FaultCounters Injected);
