using System.ComponentModel.DataAnnotations;

namespace SapB1.Mock.Configuration;

/// <summary>
/// Configuración del SAP simulado (sección <c>SapMock</c>). Las credenciales son ficticias:
/// el mock no protege ningún dato real.
/// </summary>
internal sealed class MockOptions
{
    public const string SectionName = "SapMock";

    /// <summary>Base de datos de la sociedad aceptada en el login.</summary>
    [Required]
    public string CompanyDb { get; init; } = string.Empty;

    [Required]
    public string UserName { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    /// <summary>Minutos de inactividad tras los cuales la sesión expira (SAP usa 30 por defecto).</summary>
    [Range(1, 1440)]
    public int SessionTimeoutMinutes { get; init; } = 30;

    /// <summary>Tamaño de página por defecto de las colecciones (SAP usa 20).</summary>
    [Range(1, 1000)]
    public int DefaultPageSize { get; init; } = 20;

    /// <summary>
    /// Clave opcional (de 32 a 256 caracteres sin espacios) que exigen los endpoints <c>/_mock</c> en la cabecera <c>X-Api-Key</c>.
    /// Obligatoria en la práctica si el mock es accesible desde internet. Sin ella, <c>/_mock</c> queda abierto (local y pruebas).
    /// </summary>
    [RegularExpression(@"^(\S{32,256})?$", ErrorMessage = "SapMock:AdminApiKey debe tener entre 32 y 256 caracteres sin espacios (variable SapMock__AdminApiKey).")]
    public string? AdminApiKey { get; init; }

    /// <summary>Versión del Service Layer que se informa en el login.</summary>
    [Required]
    public string Version { get; init; } = string.Empty;
}
