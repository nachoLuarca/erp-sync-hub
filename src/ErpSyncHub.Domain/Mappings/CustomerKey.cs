using System.Globalization;

namespace ErpSyncHub.Domain.Mappings;

/// <summary>Clave con la que se identifica a un cliente de la tienda en <c>customer_mappings</c>.</summary>
public static class CustomerKey
{
    /// <summary>Cliente registrado en la tienda: <c>customer:4521</c>.</summary>
    public static string ForRegistered(long storeCustomerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storeCustomerId);
        return string.Create(CultureInfo.InvariantCulture, $"customer:{storeCustomerId}");
    }

    /// <summary>Cliente invitado, identificado por su correo normalizado: <c>email:ana@example.com</c>.</summary>
    public static string ForGuest(string email) => $"email:{NormalizeEmail(email)}";

    /// <summary>Quita espacios y pasa a minúsculas, para que el mismo correo produzca siempre la misma clave.</summary>
    public static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToLowerInvariant();
    }
}
