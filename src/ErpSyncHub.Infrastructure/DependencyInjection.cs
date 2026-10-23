using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Infrastructure.Jobs;
using ErpSyncHub.Infrastructure.Mappings;
using ErpSyncHub.Infrastructure.Persistence;
using ErpSyncHub.Infrastructure.Sap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SapB1.Client;

namespace ErpSyncHub.Infrastructure;

/// <summary>Registro de los servicios de infraestructura.</summary>
public static class DependencyInjection
{
    /// <summary>Registra la persistencia (PostgreSQL) y la integración con SAP. Las opciones se validan al iniciar.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());

        services.AddDbContext<ErpSyncHubDbContext>((serviceProvider, builder) =>
            DbContextSetup.Configure(builder, serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddScoped<IMappingStore, MappingStore>();

        // Debe registrarse antes que cualquier servicio en segundo plano que use la base de datos.
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services.AddSapGateway(configuration);
    }

    /// <summary>
    /// Registra el acceso a SAP: el cliente tipado, configurado con la sección <c>SAP</c> (variables <c>SAP__BaseUrl</c>,
    /// <c>SAP__CompanyDb</c>, <c>SAP__UserName</c> y <c>SAP__Password</c>), y el adaptador que traduce sus excepciones a <c>Result</c>.
    /// </summary>
    public static IServiceCollection AddSapGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSapB1Client(configuration);
        services.AddScoped<ISapGateway, SapGateway>();
        return services;
    }
}
