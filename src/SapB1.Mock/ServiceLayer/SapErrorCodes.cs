namespace SapB1.Mock.ServiceLayer;

/// <summary>
/// Códigos de error del Service Layer que el mock reproduce. Los marcados como "aproximado"
/// no están confirmados en la documentación pública; ver docs/sap-mock.md.
/// </summary>
internal static class SapErrorCodes
{
    /// <summary>Sesión inexistente o expirada (HTTP 401).</summary>
    public const int InvalidSession = 301;

    /// <summary>Solicitud o consulta OData mal formada (HTTP 400). Aproximado.</summary>
    public const int InvalidRequest = -1;

    /// <summary>Propiedad inexistente en el cuerpo de la solicitud (HTTP 400). Aproximado.</summary>
    public const int InvalidProperty = -1000;

    /// <summary>Error genérico de validación de negocio (HTTP 400). Aproximado.</summary>
    public const int Validation = -10;

    /// <summary>Registro no encontrado, "ODBC -2028" (HTTP 404).</summary>
    public const int NotFound = -2028;

    /// <summary>El registro ya existe, "ODBC -2035" (HTTP 400). Aproximado.</summary>
    public const int AlreadyExists = -2035;

    /// <summary>Servicio no disponible (HTTP 503), solo en las caídas simuladas. Aproximado.</summary>
    public const int Unavailable = -1001;

    /// <summary>Error interno del servidor (HTTP 5xx), solo en las fallas simuladas. Aproximado.</summary>
    public const int InternalError = -1002;

    /// <summary>Credenciales o sociedad inválidas en el login (HTTP 401). Aproximado.</summary>
    public const int InvalidCredentials = -304;
}
