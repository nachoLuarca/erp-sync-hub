using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ErpSyncHub.Api.Health;

/// <summary>
/// Respuesta de <c>/health</c> en JSON: el estado general y el de cada comprobación. Solo muestra las descripciones que
/// definen los propios chequeos, nunca excepciones ni cadenas de conexión.
/// </summary>
internal static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = (long)entry.Value.Duration.TotalMilliseconds,
                }),
        };

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
