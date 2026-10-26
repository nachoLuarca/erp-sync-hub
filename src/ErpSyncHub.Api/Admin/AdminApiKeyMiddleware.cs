using ErpSyncHub.Api.Problems;

namespace ErpSyncHub.Api.Admin;

/// <summary>
/// Exige una API key válida en la cabecera <c>X-Api-Key</c> para todo lo que cuelga de <c>/admin</c>, existan o no los
/// endpoints: una ruta desconocida también responde 401, así que no se puede explorar el panel sin clave. El webhook, la salud y
/// la documentación quedan fuera (el webhook se protege con su firma). Si no hay ninguna clave configurada, el panel responde
/// 503 en lugar de quedar abierto.
/// </summary>
internal sealed partial class AdminApiKeyMiddleware(RequestDelegate next, AdminApiKeyValidator keys, ILogger<AdminApiKeyMiddleware> logger)
{
    public const string HeaderName = "X-Api-Key";
    public const string PathPrefix = "/admin";

    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments(PathPrefix))
        {
            await next(http);
            return;
        }

        if (!keys.IsConfigured)
        {
            await ProblemDetailsSetup.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "El panel está deshabilitado porque no hay ninguna clave de acceso configurada (variable de entorno Admin__ApiKey; en Compose, ERPSYNC_ADMIN_API_KEY).")
                .ExecuteAsync(http);
            return;
        }

        // Una cabecera repetida es ambigua: se rechaza en vez de elegir una.
        var presented = http.Request.Headers[HeaderName];
        if (presented.Count == 1 && keys.IsValid(presented[0]))
        {
            await next(http);
            return;
        }

        // Nunca se registra la clave presentada, ni siquiera parcial.
        LogRejected(http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress?.ToString(), presented.Count == 0 ? "sin clave" : "clave inválida");
        http.Response.Headers.WWWAuthenticate = $"ApiKey realm=\"admin\", header=\"{HeaderName}\"";
        await ProblemDetailsSetup.Problem(
            StatusCodes.Status401Unauthorized,
            $"Falta una clave válida en la cabecera {HeaderName}.")
            .ExecuteAsync(http);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acceso rechazado al panel: {Method} {Path} desde {RemoteIp} ({Reason})")]
    private partial void LogRejected(string method, PathString path, string? remoteIp, string reason);
}
