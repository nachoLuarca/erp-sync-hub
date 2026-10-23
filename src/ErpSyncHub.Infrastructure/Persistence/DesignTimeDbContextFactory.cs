using Microsoft.EntityFrameworkCore.Design;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>
/// Permite a <c>dotnet ef</c> crear el contexto sin arrancar la aplicación. Para generar migraciones no hace falta
/// conectarse; solo si se usa <c>database update</c> se lee <c>Database__ConnectionString</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ErpSyncHubDbContext>
{
    public ErpSyncHubDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString")
            ?? "Host=localhost;Database=erpsynchub_design";

        return new ErpSyncHubDbContext(DbContextSetup.BuildOptions(connectionString));
    }
}
