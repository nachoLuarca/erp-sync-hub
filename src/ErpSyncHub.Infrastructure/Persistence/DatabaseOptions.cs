using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>Configuración de la base de datos (sección <c>Database</c>).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Cadena de conexión a PostgreSQL. Contiene la contraseña: debe venir de una variable de entorno.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Aplica las migraciones pendientes al iniciar. Pensado para desarrollo y Docker; en producción con varias
    /// instancias conviene migrar como paso aparte del despliegue.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail(
                $"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.ConnectionString)} es obligatorio (variable de entorno Database__ConnectionString).")
            : ValidateOptionsResult.Success;
}
