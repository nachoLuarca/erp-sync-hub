using System.Security.Cryptography;
using System.Text;

namespace ErpSyncHub.Application.WooCommerce;

/// <summary>
/// Firma de los webhooks de WooCommerce: el HMAC-SHA256 del cuerpo crudo, con el secreto del webhook como clave,
/// codificado en Base64 y enviado en la cabecera <c>X-WC-Webhook-Signature</c>.
/// </summary>
public static class WooCommerceSignature
{
    /// <summary>Nombre de la cabecera que trae la firma.</summary>
    public const string HeaderName = "X-WC-Webhook-Signature";

    private const int Sha256Length = 32;

    /// <summary>Calcula la firma que WooCommerce enviaría para este cuerpo.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }

    /// <summary>
    /// Comprueba la firma recibida contra el cuerpo crudo, en tiempo constante. Una firma ausente, mal formada o de
    /// largo incorrecto es inválida.
    /// </summary>
    /// <param name="secret">Secreto del webhook configurado en la tienda y en el hub.</param>
    /// <param name="body">Bytes del cuerpo exactamente como llegaron; cualquier reserialización invalida la firma.</param>
    /// <param name="signatureHeader">Valor de <c>X-WC-Webhook-Signature</c>.</param>
    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        Span<byte> received = stackalloc byte[Sha256Length];
        if (!Convert.TryFromBase64String(signatureHeader.Trim(), received, out var written) || written != Sha256Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[Sha256Length];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);

        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
