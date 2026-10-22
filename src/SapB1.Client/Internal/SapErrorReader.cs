using System.Net.Http.Json;
using System.Text.Json;

namespace SapB1.Client.Internal;

internal static class SapErrorReader
{
    /// <summary>Lee el error del Service Layer del cuerpo; si no hay cuerpo JSON con ese formato, devuelve nulos.</summary>
    public static async Task<(int? Code, string? Message)> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<SapErrorEnvelope>(SapJson.Options, cancellationToken);
            return (envelope?.Error?.Code, envelope?.Error?.Message?.Value);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (NotSupportedException)
        {
            // Content-Type distinto de JSON (por ejemplo, una página HTML de un proxy).
            return (null, null);
        }
    }
}
