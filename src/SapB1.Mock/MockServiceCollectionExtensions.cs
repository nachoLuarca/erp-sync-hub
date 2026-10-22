using Microsoft.Extensions.DependencyInjection.Extensions;
using SapB1.Mock.Admin;
using SapB1.Mock.Configuration;
using SapB1.Mock.ServiceLayer;
using SapB1.Mock.Sessions;

namespace SapB1.Mock;

internal static class MockServiceCollectionExtensions
{
    public static IServiceCollection AddSapMock(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MockOptions>()
            .Bind(configuration.GetSection(MockOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Las entidades del Service Layer usan PascalCase; los errores fijan sus nombres con JsonPropertyName.
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = null;
            json.SerializerOptions.DictionaryKeyPolicy = null;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISessionStore, InMemorySessionStore>();

        return services;
    }

    public static WebApplication MapSapMock(this WebApplication app)
    {
        var serviceLayer = app.MapGroup("/b1s/v1");
        serviceLayer.MapLogin();

        var secured = serviceLayer.MapGroup(string.Empty).AddEndpointFilter<SessionRequiredFilter>();
        secured.MapLogout();

        app.MapAdmin();

        return app;
    }
}
