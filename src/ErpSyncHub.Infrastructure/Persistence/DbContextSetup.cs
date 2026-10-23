using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>Configuración común del <see cref="ErpSyncHubDbContext"/>: PostgreSQL con nombres en <c>snake_case</c>.</summary>
public static class DbContextSetup
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
    }

    /// <summary>Opciones para crear un contexto fuera de la inyección de dependencias (por ejemplo, en pruebas).</summary>
    public static DbContextOptions<ErpSyncHubDbContext> BuildOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ErpSyncHubDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }
}
