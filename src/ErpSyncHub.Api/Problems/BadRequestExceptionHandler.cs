using Microsoft.AspNetCore.Diagnostics;

namespace ErpSyncHub.Api.Problems;

/// <summary>
/// Convierte una solicitud mal formada (un JSON roto, un valor con formato incorrecto) en una respuesta 4xx con ProblemDetails.
/// Sin esto, el manejador de excepciones la trataría como un error del servidor (500) aunque la culpa sea del cliente. El texto
/// de la excepción, que está en inglés y menciona detalles internos, no se expone.
/// </summary>
internal sealed class BadRequestExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        var status = badRequest.StatusCode is >= 400 and < 500 ? badRequest.StatusCode : StatusCodes.Status400BadRequest;
        httpContext.Response.StatusCode = status;

        await ProblemDetailsSetup
            .Problem(
                ProblemDetailsSetup.Supports(status) ? status : StatusCodes.Status400BadRequest,
                "El cuerpo o los parámetros de la solicitud no tienen un formato válido: revisa que el JSON esté completo y que los valores (fechas, identificadores) tengan el tipo esperado.")
            .ExecuteAsync(httpContext);
        return true;
    }
}
