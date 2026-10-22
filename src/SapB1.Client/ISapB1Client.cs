namespace SapB1.Client;

/// <summary>
/// Punto de entrada al Service Layer de SAP Business One. Inicia sesión automáticamente en la primera
/// llamada y la renueva sola cuando SAP la da por expirada.
/// </summary>
/// <remarks>
/// Los errores de SAP se informan con <see cref="SapB1ApiException"/> y los problemas de sesión con
/// <see cref="SapB1SessionException"/>. Los fallos de red no se envuelven.
/// </remarks>
public interface ISapB1Client
{
    /// <summary>Maestro de artículos.</summary>
    IItemsApi Items { get; }

    /// <summary>
    /// Cierra la sesión en SAP. Es opcional: SAP también cierra las sesiones por inactividad.
    /// La siguiente llamada iniciará una sesión nueva. Sin sesión abierta, no hace nada.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
