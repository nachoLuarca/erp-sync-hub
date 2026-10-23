using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure;

/// <summary>Registro de los servicios de infraestructura.</summary>
public static class DependencyInjection
{
    /// <summary>Registra la persistencia (PostgreSQL) y sus opciones, que se validan al iniciar la aplicación.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());

        services.AddDbContext<ErpSyncHubDbContext>((serviceProvider, builder) =>
            DbContextSetup.Configure(builder, serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        // Debe registrarse antes que cualquier servicio en segundo plano que use la base de datos.
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }
}
