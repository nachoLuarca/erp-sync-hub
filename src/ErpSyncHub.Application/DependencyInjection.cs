using ErpSyncHub.Application.Sync;
using ErpSyncHub.Application.WooCommerce;
using Microsoft.Extensions.DependencyInjection;

namespace ErpSyncHub.Application;

/// <summary>Registro de los casos de uso.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra los casos de uso. Requiere que el contenedor tenga <see cref="WooCommerceWebhookOptions"/>,
    /// <see cref="OrderSyncOptions"/>, <see cref="TimeProvider"/> y las implementaciones de los puertos (las aporta Infrastructure).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<WooCommerceWebhookHandler>();
        services.AddScoped<OrderSyncService>();
        return services;
    }
}
