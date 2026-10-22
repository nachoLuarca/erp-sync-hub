using System.Reflection;

namespace ErpSyncHub.Domain;

/// <summary>
/// Punto de anclaje al ensamblado de dominio, para tests de arquitectura y registro de servicios.
/// </summary>
public static class AssemblyReference
{
    public static Assembly Assembly => typeof(AssemblyReference).Assembly;
}
