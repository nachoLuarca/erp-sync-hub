using System.Text.Json;
using System.Text.Json.Serialization;

namespace SapB1.Client.Internal;

/// <summary>Serialización del Service Layer: propiedades en PascalCase y sin enviar valores nulos.</summary>
internal static class SapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
