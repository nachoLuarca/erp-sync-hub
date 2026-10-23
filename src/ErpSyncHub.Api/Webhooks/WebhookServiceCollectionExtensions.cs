using ErpSyncHub.Application.WooCommerce;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Webhooks;

internal static class WebhookServiceCollectionExtensions
{
    /// <summary>Registra la configuración del webhook de WooCommerce, que se valida al iniciar la aplicación.</summary>
    public static IServiceCollection AddWooCommerceWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WooCommerceWebhookOptions>()
            .Bind(configuration.GetSection(WooCommerceWebhookOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<WooCommerceWebhookOptions>, WooCommerceWebhookOptionsValidator>());

        // Los casos de uso de Application reciben la configuración ya validada, sin depender de IOptions.
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<WooCommerceWebhookOptions>>().Value);

        return services;
    }
}
