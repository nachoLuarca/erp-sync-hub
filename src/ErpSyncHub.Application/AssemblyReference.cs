using System.Reflection;

namespace ErpSyncHub.Application;

/// <summary>
/// Punto de anclaje al ensamblado de aplicación, para tests de arquitectura y registro de servicios.
/// </summary>
public static class AssemblyReference
{
    public static Assembly Assembly => typeof(AssemblyReference).Assembly;
}
