namespace SapB1.Mock.Sessions;

/// <summary>Almacén de sesiones del Service Layer con expiración por inactividad.</summary>
internal interface ISessionStore
{
    /// <summary>Crea una sesión nueva y devuelve su identificador.</summary>
    string Create();

    /// <summary>
    /// Indica si la sesión existe y no ha expirado. Si es válida, renueva su plazo de inactividad.
    /// </summary>
    bool TryTouch(string sessionId);

    /// <summary>Cierra la sesión. Devuelve <see langword="false"/> si no existía.</summary>
    bool Remove(string sessionId);

    /// <summary>Descarta todas las sesiones, como si hubieran expirado.</summary>
    void Clear();
}
