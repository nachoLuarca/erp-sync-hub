using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpSyncHub.Domain.Mappings;

namespace ErpSyncHub.Domain.Customers;

/// <summary>
/// Genera el <c>CardCode</c> de los clientes nuevos en SAP. Los códigos son <b>deterministas</b>: el mismo cliente
/// produce siempre el mismo código, de modo que crear el cliente dos veces es detectable (SAP rechaza el duplicado)
/// y el proceso puede reanudarse sin crear clientes repetidos. Todos caben en los 15 caracteres de <c>CardCode</c>.
/// </summary>
public static class SapCardCodes
{
    /// <summary>Largo máximo del <c>CardCode</c> en SAP.</summary>
    public const int MaxLength = 15;

    /// <summary>Cliente con RUT válido: <c>C</c> + RUT sin puntos, por ejemplo <c>C12345678-5</c> (hasta 11 caracteres).</summary>
    public static string ForRut(Rut rut) => $"C{rut.Normalized}";

    /// <summary>Mayor id de cliente de la tienda que, con el prefijo <c>WC</c>, cabe en <see cref="MaxLength"/> (13 dígitos).</summary>
    public const long MaxStoreCustomerId = 9_999_999_999_999;

    /// <summary>Cliente registrado en la tienda sin RUT: <c>WC</c> + id del cliente, por ejemplo <c>WC4521</c>.</summary>
    public static string ForStoreCustomer(long storeCustomerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storeCustomerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(storeCustomerId, MaxStoreCustomerId);
        return string.Create(CultureInfo.InvariantCulture, $"WC{storeCustomerId}");
    }

    /// <summary>
    /// Cliente invitado (sin cuenta ni RUT): <c>WG</c> + 12 caracteres hexadecimales del SHA-256 del correo
    /// normalizado (14 caracteres en total).
    /// </summary>
    public static string ForGuest(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CustomerKey.NormalizeEmail(email)));
        return "WG" + Convert.ToHexString(hash)[..12];
    }
}
