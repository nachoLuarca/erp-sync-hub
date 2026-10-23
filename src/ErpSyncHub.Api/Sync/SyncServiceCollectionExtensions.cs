using ErpSyncHub.Application.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Sync;

internal static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registra la configuración de la sincronización de pedidos y el worker que procesa los jobs. La configuración se
    /// valida al iniciar la aplicación.
    /// </summary>
    public static IServiceCollection AddOrderSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrderSyncOptions>()
            .Bind(configuration.GetSection(OrderSyncOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OrderSyncOptions>, OrderSyncOptionsValidator>());
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<OrderSyncOptions>>().Value);

        services.AddOptions<SyncWorkerOptions>()
            .Bind(configuration.GetSection(SyncWorkerOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SyncWorkerOptions>, SyncWorkerOptionsValidator>());

        // Va después de la migración de la base de datos (registrada en AddInfrastructure): los servicios se inician en orden.
        services.AddHostedService<SyncWorker>();

        return services;
    }
}

internal sealed class OrderSyncOptionsValidator : IValidateOptions<OrderSyncOptions>
{
    public ValidateOptionsResult Validate(string? name, OrderSyncOptions options)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return ValidateOptionsResult.Fail(
                $"Sync:WooCommerce:TimeZoneId '{options.TimeZoneId}' no es una zona horaria válida (use un identificador IANA, por ejemplo America/Santiago).");
        }

        return ValidateOptionsResult.Success;
    }
}
