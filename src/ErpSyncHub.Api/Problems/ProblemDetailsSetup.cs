using Microsoft.AspNetCore.Http.Features;

namespace ErpSyncHub.Api.Problems;

/// <summary>Respuestas de error de la API como ProblemDetails (RFC 9457), con textos en español de Chile.</summary>
internal static class ProblemDetailsSetup
{
    private static readonly Dictionary<int, (string Title, string Type)> Known = new()
    {
        [StatusCodes.Status400BadRequest] = ("Solicitud inválida", "urn:erp-sync-hub:problem:bad-request"),
        [StatusCodes.Status401Unauthorized] = ("No autorizado", "urn:erp-sync-hub:problem:unauthorized"),
        [StatusCodes.Status404NotFound] = ("Recurso no encontrado", "urn:erp-sync-hub:problem:not-found"),
        [StatusCodes.Status409Conflict] = ("Conflicto", "urn:erp-sync-hub:problem:conflict"),
        [StatusCodes.Status405MethodNotAllowed] = ("Método no permitido", "urn:erp-sync-hub:problem:method-not-allowed"),
        [StatusCodes.Status413PayloadTooLarge] = ("Cuerpo demasiado grande", "urn:erp-sync-hub:problem:payload-too-large"),
        [StatusCodes.Status415UnsupportedMediaType] = ("Tipo de contenido no admitido", "urn:erp-sync-hub:problem:unsupported-media-type"),
        [StatusCodes.Status500InternalServerError] = ("Error interno", "urn:erp-sync-hub:problem:internal-error"),
    };

    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;

            if (Known.TryGetValue(status, out var known))
            {
                problem.Title = known.Title;
                problem.Type = known.Type;
            }

            // Un error no controlado no debe revelar detalles internos: el detalle va solo al registro.
            if (status == StatusCodes.Status500InternalServerError)
            {
                problem.Detail = "Ocurrió un error inesperado. Intenta nuevamente más tarde.";
            }

            problem.Extensions["traceId"] = context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity.TraceId.ToString()
                ?? context.HttpContext.TraceIdentifier;
        });

    public static string TitleFor(int statusCode) => Known[statusCode].Title;

    public static string TypeFor(int statusCode) => Known[statusCode].Type;

    /// <summary>Un ProblemDetails con el título y tipo estándar del estado, y un detalle específico.</summary>
    public static IResult Problem(int statusCode, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: Known[statusCode].Title,
            type: Known[statusCode].Type,
            detail: detail);
}
