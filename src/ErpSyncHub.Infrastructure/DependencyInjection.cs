using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Infrastructure.Health;
using ErpSyncHub.Infrastructure.Inventory;
using ErpSyncHub.Infrastructure.Jobs;
using ErpSyncHub.Infrastructure.Mappings;
using ErpSyncHub.Infrastructure.Persistence;
using ErpSyncHub.Infrastructure.Sap;
using ErpSyncHub.Infrastructure.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        services.AddScoped<IJobAdmin, JobAdmin>();
        services.AddScoped<IMappingStore, MappingStore>();
        services.AddScoped<IItemSnapshotStore, ItemSnapshotStore>();
        services.AddScoped<IInventorySyncRunStore, InventorySyncRunStore>();
        services.AddSingleton<IInventorySyncLock, PostgresInventorySyncLock>();

        // PostgreSQL caído es un fallo (el hub no puede guardar pedidos); SAP caído solo degrada (el inbox sigue recibiendo).
        services.AddSingleton<SapHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>(PostgresHealthCheck.Name, HealthStatus.Unhealthy)
            .Add(new HealthCheckRegistration(
                SapHealthCheck.Name,
                serviceProvider => serviceProvider.GetRequiredService<SapHealthCheck>(),
                HealthStatus.Degraded,
                tags: null));

        // Debe registrarse antes que cualquier servicio en segundo plano que use la base de datos.
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services.AddSapGateway(configuration).AddStoreGateway(configuration);
    }

    /// <summary>
    /// Registra el acceso a la tienda: el adaptador de la API REST de WooCommerce, configurado con la sección
    /// <c>Store:WooCommerce</c> (variables <c>Store__WooCommerce__BaseUrl</c>, <c>ConsumerKey</c> y <c>ConsumerSecret</c>).
    /// Si no se define ninguna, la tienda queda como no configurada (<see cref="IStoreGateway.IsConfigured"/>).
    /// </summary>
    public static IServiceCollection AddStoreGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WooCommerceOptions>().Bind(configuration.GetSection(WooCommerceOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WooCommerceOptions>, WooCommerceOptionsValidator>());

        services.AddHttpClient<IStoreGateway, WooCommerceGateway>((serviceProvider, http) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WooCommerceOptions>>().Value;
            http.Timeout = options.RequestTimeout;
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ErpSyncHub");

            if (options.IsConfigured)
            {
                http.BaseAddress = options.BaseUri;

                // WooCommerce acepta Basic con la clave y el secreto de consumidor, pero solo por HTTPS.
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{options.ConsumerKey}:{options.ConsumerSecret}")));
            }
        });

        return services;
    }

    /// <summary>
    /// Registra el acceso a SAP: el cliente tipado, configurado con la sección <c>SAP</c> (variables <c>SAP__BaseUrl</c>,
    /// <c>SAP__CompanyDb</c>, <c>SAP__UserName</c> y <c>SAP__Password</c>), y el adaptador que traduce sus excepciones a <c>Result</c>.
    /// </summary>
    public static IServiceCollection AddSapGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSapB1Client(configuration);
        services.AddOptions<SapGatewayOptions>().Bind(configuration.GetSection(SapB1Options.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SapGatewayOptions>, SapGatewayOptionsValidator>());
        services.AddScoped<ISapGateway, SapGateway>();
        return services;
    }
}
