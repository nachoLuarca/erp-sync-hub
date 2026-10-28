// Estas pruebas registran a propósito valores y plantillas arbitrarias para comprobar qué llega al destino: los avisos sobre el costo de los
// argumentos de log y las plantillas dinámicas no aplican.
#pragma warning disable CA1873, CA1861, CA2254

using System.Text;
using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Logging;

/// <summary>La política de logs impide que un campo personal llegue a un destino, aunque alguien lo agregue a un mensaje.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class PersonalDataLoggingTests(PostgresFixture postgres)
{
    private async Task<(WebhookHost Host, CollectingSink Sink, ILogger Logger)> StartAsync()
    {
        var sink = new CollectingSink();
        var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        return (host, sink, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Prueba"));
    }

    private static WooCommerceOrder Order() =>
        WooCommerceOrderParser.Parse(Encoding.UTF8.GetString(Fixtures.ReadBytes("woocommerce/order-processing.json"))).Value;

    private static string Render(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        logEvent.RenderMessage(writer);
        foreach (var property in logEvent.Properties)
        {
            writer.Write(' ');
            writer.Write(property.Key);
            writer.Write('=');
            property.Value.Render(writer);
        }

        return writer.ToString();
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("email")]
    [InlineData("Phone")]
    [InlineData("Rut")]
    [InlineData("TaxId")]
    [InlineData("FederalTaxID")]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("CardName")]
    [InlineData("Address1")]
    [InlineData("Billing")]
    [InlineData("Payload")]
    public async Task Una_propiedad_con_nombre_de_dato_personal_se_oculta_sea_cual_sea_su_valor(string name)
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;

        logger.LogInformation("Dato {" + name + "} registrado", "Valentina Muñoz, Av. Providencia 1234");

        var logged = sink.Events.Single(logEvent => logEvent.Properties.ContainsKey(name));
        logged.Properties[name].ToString().ShouldBe("\"[personal]\"");
        Render(logged).ShouldNotContain("Valentina");
        Render(logged).ShouldNotContain("Providencia");
    }

    [Fact]
    public async Task Un_RUT_o_un_correo_en_cualquier_propiedad_se_ocultan()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;

        logger.LogInformation("Falló {Detalle} para {Cliente}", "Business partner 'C18765432-7' no existe", "contacto valentina@example.com");

        var rendered = Render(sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("Detalle")));
        rendered.ShouldNotContain("18765432");
        rendered.ShouldNotContain("valentina");
        rendered.ShouldContain("[RUT]");
        rendered.ShouldContain("[correo]");
    }

    [Fact]
    public async Task Un_objeto_del_pedido_registrado_con_arroba_no_se_desarma_en_sus_campos()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;

        logger.LogInformation("Pedido {@Pedido}", Order());

        var logged = sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("Pedido"));
        logged.Properties["Pedido"].ToString().ShouldBe("\"[personal]\"");
        Render(logged).ShouldNotContain("Valentina");
        Render(logged).ShouldNotContain("valentina@example.com");
    }

    [Fact]
    public async Task Un_objeto_del_pedido_registrado_como_texto_tampoco_muestra_sus_campos()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;

        // Sin la arroba, Serilog usa ToString del objeto (el de un record imprime todos sus campos).
        logger.LogInformation("Pedido {Pedido}", Order());

        var logged = sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("Pedido"));
        Render(logged).ShouldNotContain("Valentina");
        Render(logged).ShouldNotContain("+56912345678");
        Render(logged).ShouldContain("[personal]");
    }

    [Fact]
    public async Task Un_objeto_anonimo_conserva_sus_campos_normales_y_oculta_los_personales()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;

        logger.LogInformation("Resumen {@Resumen}", new { Total = 24990, Email = "valentina@example.com", Items = new[] { "A00001" } });

        var rendered = Render(sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("Resumen")));
        rendered.ShouldContain("24990");
        rendered.ShouldContain("A00001");
        rendered.ShouldNotContain("valentina@example.com");
        rendered.ShouldContain("[personal]");
    }

    [Fact]
    public async Task Los_datos_del_pedido_en_proceso_se_ocultan_en_cualquier_texto_que_se_registre()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;
        using var scope = PersonalDataScope.Begin([new PersonalValue(PersonalDataKind.Name, "Valentina Muñoz")]);

        logger.LogInformation("SAP respondió {Respuesta}", "cliente VALENTINA MUNOZ congelado");

        Render(sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("Respuesta"))).ShouldNotContain("VALENTINA");
    }

    [Fact]
    public async Task Lo_que_no_es_personal_no_se_toca()
    {
        var (host, sink, logger) = await StartAsync();
        await using var _ = host;
        var jobId = Guid.NewGuid();

        logger.LogInformation("Job {JobId} con {Count} líneas en {Estado}", jobId, 3, "succeeded");

        var logged = sink.Events.Single(logEvent => logEvent.Properties.ContainsKey("JobId"));
        logged.Properties["JobId"].ToString().ShouldBe($"{jobId}");
        logged.Properties["Count"].ToString().ShouldBe("3");
        logged.Properties["Estado"].ToString().ShouldBe("\"succeeded\"");
    }
}
