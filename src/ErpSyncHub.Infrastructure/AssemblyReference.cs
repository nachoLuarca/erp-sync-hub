using System.Reflection;

namespace ErpSyncHub.Infrastructure;

/// <summary>
/// Punto de anclaje al ensamblado de infraestructura, para tests de arquitectura y registro de servicios.
/// </summary>
public static class AssemblyReference
{
    public static Assembly Assembly => typeof(AssemblyReference).Assembly;
}
