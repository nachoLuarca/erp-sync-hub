using System.Text.Json;
using System.Text.Json.Nodes;

namespace SapB1.Mock.OData;

/// <summary>Serialización de las entidades del Service Layer: propiedades en PascalCase.</summary>
internal static class SapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
    };

    /// <summary>Convierte una entidad a su representación JSON.</summary>
    public static JsonObject ToNode<T>(T entity) =>
        JsonSerializer.SerializeToNode(entity, Options)!.AsObject();
}
