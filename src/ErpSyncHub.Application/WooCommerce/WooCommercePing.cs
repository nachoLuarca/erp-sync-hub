using System.Text;

namespace ErpSyncHub.Application.WooCommerce;

/// <summary>
/// Al crear un webhook, WooCommerce envía un "ping" para comprobar que la URL responde. No es JSON: es un formulario
/// (<c>application/x-www-form-urlencoded</c>) con el cuerpo <c>webhook_id=123</c>.
/// </summary>
public static class WooCommercePing
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("webhook_id=");

    /// <summary>Indica si el cuerpo es el ping de creación de un webhook.</summary>
    public static bool IsPing(ReadOnlySpan<byte> body)
    {
        var trimmed = body.Trim((byte)' ').Trim((byte)'\r').Trim((byte)'\n');
        if (!trimmed.StartsWith(Prefix))
        {
            return false;
        }

        var id = trimmed[Prefix.Length..];
        return id.Length > 0 && id.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;
    }
}
