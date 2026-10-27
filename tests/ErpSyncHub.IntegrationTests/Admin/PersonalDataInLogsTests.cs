using System.Text;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>
/// El payload de un pedido trae datos personales (nombre, correo, teléfono, dirección, RUT, IP). Esos datos pueden verse en el
/// panel con <c>includePayload=true</c>, pero no deben aparecer en ningún log. Los eventos que recoge el <see cref="CollectingSink"/>
/// son los mismos que Serilog envía a Seq y a la consola: lo que no está aquí no llega a Seq.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public class PersonalDataInLogsTests(PostgresFixture postgres)
{
    /// <summary>Datos personales del pedido de ejemplo (tests/Fixtures/woocommerce/order-processing.json).</summary>
    private static readonly string[] PersonalData =
    [
        "Valentina",
        "Muñoz",
        "valentina@example.com",
        "+56912345678",
        "56912345678",
        "Av. Providencia",
        "Depto 56",
        "7500000",
        "18.765.432-7",
        "18765432",
        "190.96.10.20",
        "Entregar en conserjería",
    ];

    private static string Order => Fixtures.Read("woocommerce/order-processing.json");

    private async Task<(WebhookHost Host, CollectingSink Sink, SapMockHost Sap)> StartAsync(
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null,
        Func<string, Task>? seed = null)
    {
        var sink = new CollectingSink();
        var sap = new SapMockHost();
        var host = await WebhookHost.StartAsync(
            postgres,
            builder =>
            {
                builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink));
                configure?.Invoke(builder);
            },
            sap: sap,
            workerEnabled: true,
            seed: seed);
        return (host, sink, sap);
    }

    private static async Task ReadPanelAsync(WebhookHost host)
    {
        await host.Client.GetAsync("/admin/jobs");
        var id = await host.ScalarAsync<Guid>("SELECT id FROM sync_jobs LIMIT 1");
        await host.Client.GetAsync($"/admin/jobs/{id}?includePayload=true");
        await host.Client.GetAsync($"/admin/jobs/{id}");
    }

    private static void AssertNoPersonalData(CollectingSink sink, params string[] extra)
    {
        var rendered = sink.Events.Select(Render).ToList();
        rendered.ShouldNotBeEmpty();

        foreach (var datum in PersonalData.Concat(extra))
        {
            var leaks = rendered.Where(text => text.Contains(datum, StringComparison.OrdinalIgnoreCase)).ToList();
            leaks.ShouldBeEmpty($"El dato personal '{datum}' aparece en el log: {leaks.FirstOrDefault()}");
        }
    }

    [Fact]
    public async Task Un_pedido_que_se_sincroniza_no_deja_datos_personales_en_los_logs()
    {
        var (host, sink, sap) = await StartAsync();
        using var _ = sap;
        await using var __ = host;

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink);
        sink.Events.ShouldContain(logEvent => logEvent.RenderMessage().Contains("Job completado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_RUT_invalido_no_se_copia_a_los_logs_ni_al_error_del_job()
    {
        var (host, sink, sap) = await StartAsync();
        using var _ = sap;
        await using var __ = host;

        await host.PostSignedAsync(Order.Replace("18.765.432-7", "18.765.432-1", StringComparison.Ordinal));
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink, "18.765.432-1");
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.invalid_tax_id");
        (await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs")).ShouldNotBeNull().ShouldNotContain("18.765.432-1");
    }

    [Fact]
    public async Task Un_cliente_inhabilitado_en_SAP_no_deja_su_nombre_ni_su_RUT_en_los_logs()
    {
        var (host, sink, sap) = await StartAsync();
        using var _ = sap;
        await using var __ = host;
        await sap.PostAsync("BusinessPartners", new { CardCode = "C18765432-7", CardName = "Valentina Muñoz Inhabilitada", FederalTaxID = "18.765.432-7", Frozen = "tYES" });

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink, "Inhabilitada");
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.inactive_customer");
    }

    [Fact]
    public async Task El_error_de_SAP_que_repite_el_codigo_del_cliente_no_filtra_el_RUT_ni_a_los_logs_ni_al_job()
    {
        // El hub recuerda que el cliente 4521 es C18765432-7, pero ese cliente ya no existe en SAP: SAP responde "Business partner
        // 'C18765432-7' does not exist", y el código de un cliente nuevo se deriva de su RUT.
        var (host, sink, sap) = await StartAsync(seed: async connectionString =>
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(
                "INSERT INTO customer_mappings (id, external_key, card_code, created_at) VALUES (gen_random_uuid(), 'customer:4521', 'C18765432-7', now())",
                connection);
            await command.ExecuteNonQueryAsync();
        });
        using var _ = sap;
        await using var __ = host;

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");
        await ReadPanelAsync(host);

        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("sap.rejected");
        var stored = await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs");
        stored.ShouldNotBeNull().ShouldContain("does not exist"); // el motivo se conserva
        stored.ShouldNotContain("18765432");
        AssertNoPersonalData(sink);
    }

    [Fact]
    public async Task Un_SKU_desconocido_no_deja_datos_personales_en_los_logs()
    {
        var (host, sink, sap) = await StartAsync();
        using var _ = sap;
        await using var __ = host;

        await host.PostSignedAsync(Order.Replace("\"sku\": \"A00001\"", "\"sku\": \"NO-EXISTE\"", StringComparison.Ordinal));
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink);
    }

    [Fact]
    public async Task Los_reintentos_por_una_caida_de_SAP_no_dejan_datos_personales_en_los_logs()
    {
        var (host, sink, sap) = await StartAsync(builder =>
        {
            builder.UseSetting("Worker:Retry:BaseDelay", "00:00:00.050");
            builder.UseSetting("Worker:Retry:MaxDelay", "00:00:00.100");
            builder.UseSetting("Worker:Retry:MaxAttempts", "4");
        });
        using var _ = sap;
        await using var __ = host;
        await sap.SetFaultsAsync(new { Outage = true });

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'dead_letter') FROM sync_jobs", "que el job llegue al dead letter");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink);
        sink.Events.ShouldContain(logEvent => logEvent.Level == LogEventLevel.Error && logEvent.RenderMessage().Contains("dead letter", StringComparison.Ordinal));
        sink.Events.ShouldContain(logEvent => logEvent.RenderMessage().Contains("Se reintentará", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_payload_incompleto_tampoco_filtra_su_contenido_en_los_logs()
    {
        var (host, sink, sap) = await StartAsync();
        using var _ = sap;
        await using var __ = host;

        // Un JSON de pedido incompleto: el parser lo rechaza sin volcar su contenido.
        await host.ExecuteAsync(
            "INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, cycle_attempts, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), 'woocommerce', '777', 'order', '{\"billing\": {\"first_name\": \"Valentina\", \"email\": \"valentina@example.com\"}}', 'pending', 0, 0, now(), now())");
        await host.WaitUntilAsync("SELECT bool_and(status <> 'pending' AND status <> 'processing') FROM sync_jobs", "que el worker procese el job");
        await ReadPanelAsync(host);

        AssertNoPersonalData(sink);
    }

    private static string Render(LogEvent logEvent)
    {
        var builder = new StringBuilder(logEvent.RenderMessage());
        foreach (var property in logEvent.Properties)
        {
            builder.Append(' ').Append(property.Key).Append('=').Append(property.Value);
        }

        if (logEvent.Exception is not null)
        {
            builder.Append(' ').Append(logEvent.Exception);
        }

        return builder.ToString();
    }
}
