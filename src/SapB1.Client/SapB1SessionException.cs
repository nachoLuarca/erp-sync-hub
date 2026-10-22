using System.Net;

namespace SapB1.Client;

/// <summary>
/// No se pudo iniciar sesión (credenciales o sociedad inválidas, servidor que rechaza el login)
/// o SAP siguió rechazando la sesión tras renovarla.
/// </summary>
public sealed class SapB1SessionException : SapB1Exception
{
    public SapB1SessionException(string message, HttpStatusCode? statusCode, int? sapErrorCode, string? sapErrorMessage)
        : base(
            statusCode is { } status ? Describe(message, status, sapErrorCode, sapErrorMessage) : message,
            statusCode,
            sapErrorCode,
            sapErrorMessage)
    {
    }
}
