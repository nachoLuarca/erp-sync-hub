using System.Net;

namespace SapB1.Client;

/// <summary>SAP rechazó la operación: validación, registro inexistente o duplicado, solicitud inválida, error del servidor.</summary>
public sealed class SapB1ApiException : SapB1Exception
{
    public SapB1ApiException(HttpStatusCode statusCode, int? sapErrorCode, string? sapErrorMessage)
        : base(
            Describe("SAP rejected the request", statusCode, sapErrorCode, sapErrorMessage),
            statusCode,
            sapErrorCode,
            sapErrorMessage)
    {
    }

    /// <summary>Código HTTP de la respuesta.</summary>
    public new HttpStatusCode StatusCode => base.StatusCode!.Value;

    /// <summary>El registro pedido no existe.</summary>
    public bool IsNotFound => SapErrorCode == SapB1ErrorCodes.NotFound;

    /// <summary>El registro que se intentó crear ya existe.</summary>
    public bool IsAlreadyExists => SapErrorCode == SapB1ErrorCodes.AlreadyExists;
}
