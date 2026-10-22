using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SapB1.Client.Tests;

public class ConfigurationTests
{
    [Fact]
    public void La_configuracion_se_lee_de_la_seccion_SAP()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAP:BaseUrl"] = "https://sap.empresa.cl:50000",
                ["SAP:CompanyDb"] = "SBO_EMPRESA",
                ["SAP:UserName"] = "integracion",
                ["SAP:Password"] = "secreto",
                ["SAP:RequestTimeout"] = "00:00:10",
                ["SAP:PageSize"] = "50",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSapB1Client(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SapB1Options>>().Value;

        options.BaseUrl.ShouldBe("https://sap.empresa.cl:50000");
        options.CompanyDb.ShouldBe("SBO_EMPRESA");
        options.UserName.ShouldBe("integracion");
        options.Password.ShouldBe("secreto");
        options.RequestTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.PageSize.ShouldBe(50);
    }

    [Fact]
    public void El_cliente_apunta_al_Service_Layer_v1_sin_importar_la_barra_final_de_BaseUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAP:BaseUrl"] = "https://sap.empresa.cl:50000/",
                ["SAP:CompanyDb"] = "SBO_EMPRESA",
                ["SAP:UserName"] = "integracion",
                ["SAP:Password"] = "secreto",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSapB1Client(configuration);
        using var provider = services.BuildServiceProvider();

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ISapB1Client));

        http.BaseAddress.ShouldBe(new Uri("https://sap.empresa.cl:50000/b1s/v1/"));
    }

    [Fact]
    public void Se_puede_usar_otra_seccion_de_configuracion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Erp:BaseUrl"] = "http://localhost:5100",
                ["Erp:CompanyDb"] = "SBODemoCL",
                ["Erp:UserName"] = "manager",
                ["Erp:Password"] = "manager",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSapB1Client(configuration, "Erp");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<SapB1Options>>().Value.BaseUrl.ShouldBe("http://localhost:5100");
    }

    [Theory]
    [InlineData("BaseUrl", "no-es-una-url", "BaseUrl")]
    [InlineData("BaseUrl", "ftp://sap.empresa.cl", "BaseUrl")]
    [InlineData("BaseUrl", "", "BaseUrl")]
    [InlineData("CompanyDb", "", "CompanyDb")]
    [InlineData("UserName", " ", "UserName")]
    [InlineData("Password", "", "Password")]
    [InlineData("RequestTimeout", "00:00:00", "RequestTimeout")]
    [InlineData("PageSize", "0", "PageSize")]
    public void Una_configuracion_invalida_se_rechaza_con_un_mensaje_claro(string key, string value, string expectedInMessage)
    {
        var values = new Dictionary<string, string?>
        {
            ["SAP:BaseUrl"] = "http://localhost:5100",
            ["SAP:CompanyDb"] = "SBODemoCL",
            ["SAP:UserName"] = "manager",
            ["SAP:Password"] = "manager",
        };
        values[$"SAP:{key}"] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSapB1Client(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SapB1Options>>().Value);

        exception.Message.ShouldContain(expectedInMessage);
    }

    [Fact]
    public void Los_argumentos_obligatorios_se_validan()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentNullException>(() => services.AddSapB1Client((IConfiguration)null!));
        Should.Throw<ArgumentNullException>(() => services.AddSapB1Client((Action<SapB1Options>)null!));
    }
}
