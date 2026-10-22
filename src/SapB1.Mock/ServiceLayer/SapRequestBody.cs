using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SapB1.Mock.ServiceLayer;

/// <summary>Lectura de cuerpos de solicitud con las validaciones del Service Layer.</summary>
internal static class SapRequestBody
{
    /// <summary>
    /// Lee el cuerpo como objeto JSON y rechaza las propiedades que no existen en <typeparamref name="TEntity"/>,
    /// como hace SAP ("Property 'X' of 'Entidad' is invalid").
    /// </summary>
    public static async Task<(JsonObject? Body, IResult? Failure)> ReadObjectAsync<TEntity>(
        HttpContext http,
        CancellationToken cancellationToken)
    {
        JsonNode? node;
        try
        {
            node = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is not JsonObject body)
        {
            return (null, InvalidJson());
        }

        var unknown = body.Select(p => p.Key).FirstOrDefault(name => !EntityProperties<TEntity>.Names.Contains(name));
        if (unknown is not null)
        {
            return (null, SapResults.Error(
                StatusCodes.Status400BadRequest,
                SapErrorCodes.InvalidProperty,
                $"Property '{unknown}' of '{typeof(TEntity).Name}' is invalid"));
        }

        return (body, null);
    }

    /// <summary>Convierte el JSON a la entidad; un tipo incorrecto en alguna propiedad es una solicitud inválida.</summary>
    public static (TEntity? Entity, IResult? Failure) ToEntity<TEntity>(JsonObject body)
        where TEntity : class
    {
        try
        {
            var entity = body.Deserialize<TEntity>(OData.SapJson.Options);
            return entity is null ? (null, InvalidJson()) : (entity, null);
        }
        catch (JsonException)
        {
            return (null, InvalidJson());
        }
    }

    public static IResult InvalidJson() =>
        SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.InvalidRequest, "Invalid JSON format.");

    private static class EntityProperties<TEntity>
    {
        public static readonly HashSet<string> Names = typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }
}
