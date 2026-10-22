using System.Text.Json.Serialization;

namespace SapB1.Mock.ServiceLayer;

// Los errores del Service Layer usan claves en minúscula, a diferencia de las entidades (PascalCase).

internal sealed record SapErrorResponse([property: JsonPropertyName("error")] SapErrorBody Error);

internal sealed record SapErrorBody(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] SapErrorMessage Message);

internal sealed record SapErrorMessage(
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("value")] string Value);
