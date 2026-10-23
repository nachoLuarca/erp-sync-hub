using System.Buffers;

namespace ErpSyncHub.Api.Webhooks;

internal static class BoundedBody
{
    /// <summary>
    /// Lee el cuerpo de la solicitud completo, tal como llegó, sin pasar de <paramref name="maxBytes"/>.
    /// Devuelve <see langword="null"/> si el cuerpo es más grande, sin leer el resto: un webhook no debe poder
    /// agotar la memoria del servidor.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
