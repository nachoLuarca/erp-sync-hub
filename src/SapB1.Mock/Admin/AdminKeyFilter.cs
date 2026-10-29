using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;

namespace SapB1.Mock.Admin;

/// <summary>
/// Protege los endpoints <c>/_mock</c> con la cabecera <c>X-Api-Key</c> cuando <c>SapMock:AdminApiKey</c> está definida.
/// Sin clave configurada no hace nada: así funciona en local y en las pruebas. Un mock expuesto a internet debe definirla,
/// porque <c>/_mock</c> permite borrar los datos e inyectar fallas.
/// </summary>
internal sealed class AdminKeyFilter(IOptions<MockOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[]? _expected = string.IsNullOrWhiteSpace(options.Value.AdminApiKey)
        ? null
        : SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.AdminApiKey));

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (_expected is null)
        {
            return next(context);
        }

        // Se comparan los SHA-256 en tiempo constante: no se filtra el largo ni la posición en que difieren.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers[HeaderName].ToString()));
        if (CryptographicOperations.FixedTimeEquals(presented, _expected))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Clave de administración inválida",
            detail: $"Los endpoints /_mock exigen la cabecera {HeaderName} con la clave configurada en SapMock__AdminApiKey."));
    }
}
