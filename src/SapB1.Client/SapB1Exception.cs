using System.Net;

namespace SapB1.Client;

/// <summary>Base de las excepciones que el cliente lanza por respuestas de SAP.</summary>
/// <remarks>
/// Los fallos de transporte (red, tiempo de espera, cancelación) no se envuelven:
/// se propagan como <see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>
/// u <see cref="OperationCanceledException"/>.
/// </remarks>
public abstract class SapB1Exception : Exception
{
    private protected SapB1Exception(string message, HttpStatusCode? statusCode, int? sapErrorCode, string? sapErrorMessage)
        : base(message)
    {
        StatusCode = statusCode;
        SapErrorCode = sapErrorCode;
        SapErrorMessage = sapErrorMessage;
    }

    /// <summary>Código HTTP de la respuesta, si hubo respuesta.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Código de error del Service Layer (<c>error.code</c>), si la respuesta lo traía.</summary>
    public int? SapErrorCode { get; }

    /// <summary>Mensaje de error del Service Layer (<c>error.message.value</c>), si la respuesta lo traía.</summary>
    public string? SapErrorMessage { get; }

    private protected static string Describe(string prefix, HttpStatusCode statusCode, int? code, string? message)
    {
        var detail = code is null && message is null ? string.Empty : $": [{code}] {message}";
        return $"{prefix} (HTTP {(int)statusCode}){detail}";
    }
}
