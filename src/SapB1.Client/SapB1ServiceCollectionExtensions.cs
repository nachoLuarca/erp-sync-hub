using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SapB1.Client.Internal;

namespace SapB1.Client;

/// <summary>Registro del cliente del Service Layer en la inyección de dependencias.</summary>
public static class SapB1ServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="ISapB1Client"/> leyendo la configuración de la sección indicada
    /// (por defecto <c>SAP</c>, por ejemplo <c>SAP__BaseUrl</c> como variable de entorno).
    /// La configuración se valida al iniciar la aplicación.
    /// </summary>
    /// <returns>El constructor del <c>HttpClient</c>, para agregar políticas de resiliencia o handlers.</returns>
    public static IHttpClientBuilder AddSapB1Client(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = SapB1Options.SectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SapB1Options>().Bind(configuration.GetSection(sectionName));
        return services.AddSapB1ClientCore();
    }

    /// <summary>Registra <see cref="ISapB1Client"/> configurándolo por código.</summary>
    /// <returns>El constructor del <c>HttpClient</c>, para agregar políticas de resiliencia o handlers.</returns>
    public static IHttpClientBuilder AddSapB1Client(this IServiceCollection services, Action<SapB1Options> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SapB1Options>().Configure(configure);
        return services.AddSapB1ClientCore();
    }

    private static IHttpClientBuilder AddSapB1ClientCore(this IServiceCollection services)
    {
        services.AddOptions<SapB1Options>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SapB1Options>, SapB1OptionsValidator>());

        // La sesión debe sobrevivir al reciclado de handlers de IHttpClientFactory: va en un singleton.
        services.TryAddSingleton<SapB1SessionStore>();
        services.TryAddTransient<SapB1SessionHandler>();

        return services
            .AddHttpClient<ISapB1Client, SapB1Client>((serviceProvider, http) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<SapB1Options>>().Value;
                http.BaseAddress = options.ServiceLayerUri;
                http.Timeout = options.RequestTimeout;
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .AddHttpMessageHandler<SapB1SessionHandler>();
    }
}
