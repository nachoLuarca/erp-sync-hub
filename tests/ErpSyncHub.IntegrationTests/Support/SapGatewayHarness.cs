using ErpSyncHub.Application.Sap;
using ErpSyncHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapB1.Client;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// El adaptador de SAP tal como lo usa la aplicación (obtenido de la inyección de dependencias), conectado al SAP
/// simulado en memoria o a un <see cref="ISapB1Client"/> sustituto.
/// </summary>
internal sealed class SapGatewayHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SapMockHost? _mock;

    private SapGatewayHarness(ServiceProvider provider, SapMockHost? mock)
    {
        _provider = provider;
        _mock = mock;
    }

    public ISapGateway Gateway => _provider.CreateScope().ServiceProvider.GetRequiredService<ISapGateway>();

    /// <summary>El cliente real de SAP, para preparar datos en el mock sin pasar por el adaptador.</summary>
    public ISapB1Client Client => _provider.GetRequiredService<ISapB1Client>();

    public SapMockHost Mock => _mock ?? throw new InvalidOperationException("Este arnés no usa el SAP simulado.");

    /// <summary>Adaptador conectado al SAP simulado en memoria.</summary>
    public static SapGatewayHarness AgainstMock(string password = SapCredentials.Password, string? taxIdFormat = null)
    {
        var mock = new SapMockHost();
        var services = BaseServices(password, taxIdFormat);
        services.AddHttpClient(nameof(ISapB1Client)).ConfigurePrimaryHttpMessageHandler(mock.CreateHandler);
        return new SapGatewayHarness(services.BuildServiceProvider(), mock);
    }

    /// <summary>Adaptador conectado a un cliente de SAP sustituto, para provocar errores a voluntad.</summary>
    public static SapGatewayHarness WithClient(ISapB1Client client)
    {
        var services = BaseServices(SapCredentials.Password, taxIdFormat: null);
        services.AddSingleton(client);
        return new SapGatewayHarness(services.BuildServiceProvider(), mock: null);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _mock?.Dispose();
    }

    private static ServiceCollection BaseServices(string password, string? taxIdFormat)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SAP:BaseUrl"] = "http://localhost",
            ["SAP:CompanyDb"] = SapCredentials.CompanyDb,
            ["SAP:UserName"] = SapCredentials.UserName,
            ["SAP:Password"] = password,
        };
        if (taxIdFormat is not null)
        {
            settings["SAP:TaxIdFormat"] = taxIdFormat;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSapGateway(configuration);
        return services;
    }
}
