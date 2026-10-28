namespace SapB1.Client;

/// <summary>Códigos de error del Service Layer (<c>error.code</c>) que el cliente interpreta.</summary>
public static class SapB1ErrorCodes
{
    /// <summary>Sesión inexistente o expirada.</summary>
    public const int InvalidSession = 301;

    /// <summary>Usuario, contraseña o sociedad inválidos al iniciar sesión ("Fail to get DB credentials").</summary>
    public const int InvalidCredentials = -304;

    /// <summary>Registro no encontrado ("No matching records found (ODBC -2028)").</summary>
    public const int NotFound = -2028;

    /// <summary>El registro ya existe ("This entry already exists in the following tables (ODBC -2035)").</summary>
    public const int AlreadyExists = -2035;
}
