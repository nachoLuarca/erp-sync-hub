using System.Text.Json.Serialization;

namespace SapB1.Client.Internal;

/// <summary>Página de una colección OData v3.</summary>
internal sealed class ODataPage<T>
{
    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; init; } = [];

    /// <summary>Ruta relativa a la raíz del servicio para pedir la página siguiente.</summary>
    [JsonPropertyName("odata.nextLink")]
    public string? NextLink { get; init; }
}

internal sealed record LoginRequest(string CompanyDB, string UserName, string Password);

internal sealed record LoginResponse
{
    public string? SessionId { get; init; }

    public int? SessionTimeout { get; init; }
}

// Los errores del Service Layer usan claves en minúscula.

internal sealed record SapErrorEnvelope([property: JsonPropertyName("error")] SapErrorDetail? Error);

internal sealed record SapErrorDetail(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] SapErrorText? Message);

internal sealed record SapErrorText([property: JsonPropertyName("value")] string? Value);
