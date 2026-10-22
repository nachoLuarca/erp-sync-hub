using System.Reflection;

namespace SapB1.Client;

/// <summary>
/// Punto de anclaje al ensamblado del cliente de SAP B1, para tests de arquitectura y registro de servicios.
/// </summary>
public static class AssemblyReference
{
    public static Assembly Assembly => typeof(AssemblyReference).Assembly;
}
