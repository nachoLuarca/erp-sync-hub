using System.Net;
using System.Text.Json;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>El circuit breaker se ve en el panel, en /admin/metrics, en /health y en el log, y una persona puede cerrarlo.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class CircuitBreakerPanelTests(PostgresFixture postgres)
{
    private const string OpenSql =
        """
        INSERT INTO circuit_breakers (name, state, consecutive_failures, reason, opened_at, open_count, next_probe_at, last_error_code, last_error_message, updated_at)
        VALUES ('sap', 'open', 5, 'transient_failures', now() - interval '2 minutes', 2, now() + interval '10 minutes', 'sap.unreachable', 'No se pudo conectar con SAP.', now())
        """;

    private async Task<WebhookHost> StartAsync(bool open, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null)
    {
        var host = await WebhookHost.StartAsync(
            postgres,
            builder =>
            {
                builder.UseSetting("Admin:ReadApiKeys", ApiFactory.ReadApiKey);
                configure?.Invoke(builder);
            });
        if (open)
        {
            await host.ExecuteAsync(OpenSql);
        }

        return host;
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // --- Listado y métricas ---

    [Fact]
    public async Task Sin_uso_el_circuito_de_SAP_figura_cerrado()
    {
        await using var host = await StartAsync(open: false);

        using var circuits = await ReadAsync(host.Client, "/admin/circuit-breakers");

        var sap = circuits.RootElement.EnumerateArray().ShouldHaveSingleItem();
        sap.GetProperty("name").GetString().ShouldBe("sap");
        sap.GetProperty("state").GetString().ShouldBe("closed");
        sap.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Abierto_muestra_el_motivo_cuando_toca_la_prueba_y_el_ultimo_error_y_lo_puede_leer_una_clave_de_lectura()
    {
        await using var host = await StartAsync(open: true);
        using var reader = host.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadApiKey);

        using var circuits = await ReadAsync(reader, "/admin/circuit-breakers");

        var sap = circuits.RootElement.EnumerateArray().Single();
        sap.GetProperty("state").GetString().ShouldBe("open");
        sap.GetProperty("reason").GetString().ShouldBe("transient_failures");
        sap.GetProperty("openCount").GetInt32().ShouldBe(2);
        sap.GetProperty("nextProbeAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        sap.GetProperty("lastErrorCode").GetString().ShouldBe("sap.unreachable");
    }

    [Fact]
    public async Task Las_metricas_incluyen_el_estado_de_los_circuitos()
    {
        await using var host = await StartAsync(open: true);

        using var metrics = await ReadAsync(host.Client, "/admin/metrics");

        var sap = metrics.RootElement.GetProperty("circuits").EnumerateArray().Single();
        sap.GetProperty("name").GetString().ShouldBe("sap");
        sap.GetProperty("state").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Las_metricas_con_el_circuito_sano_lo_muestran_cerrado()
    {
        await using var host = await StartAsync(open: false);

        using var metrics = await ReadAsync(host.Client, "/admin/metrics");

        metrics.RootElement.GetProperty("circuits").EnumerateArray().Single().GetProperty("state").GetString().ShouldBe("closed");
    }

    // --- Salud ---

    [Fact]
    public async Task Con_el_circuito_abierto_la_salud_queda_Degraded_con_el_motivo_y_sin_llamar_a_SAP()
    {
        // Sin SAP simulado: si la sonda llamara a SAP, la descripción hablaría de no poder conectar, no del circuito.
        await using var host = await StartAsync(open: true);

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK); // SAP caído solo degrada: el hub sigue recibiendo pedidos
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Degraded");
        var sap = body.RootElement.GetProperty("checks").GetProperty("sap");
        sap.GetProperty("status").GetString().ShouldBe("Degraded");
        sap.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("circuito de SAP está abierto");
        sap.GetProperty("data").GetProperty("circuitState").GetString().ShouldBe("open");
        sap.GetProperty("data").GetProperty("circuitReason").GetString().ShouldBe("transient_failures");
    }

    [Fact]
    public async Task Con_credenciales_rechazadas_la_salud_explica_que_hay_que_revisarlas()
    {
        await using var host = await StartAsync(open: false);
        await host.ExecuteAsync(OpenSql.Replace("'transient_failures'", "'credentials_rejected'", StringComparison.Ordinal));

        using var body = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/health"));

        var description = body.RootElement.GetProperty("checks").GetProperty("sap").GetProperty("description").GetString()!;
        description.ShouldContain("credenciales");
        description.ShouldContain("SAP__Password");
        description.ShouldNotContain("contrasena");
    }

    // --- Cierre manual ---

    [Fact]
    public async Task Cerrar_el_circuito_lo_deja_cerrado_para_todas_las_instancias_y_queda_en_la_auditoria()
    {
        await using var host = await StartAsync(open: true);

        var response = await host.Client.PostAsync("/admin/circuit-breakers/sap/close", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("state").GetString().ShouldBe("closed");
        (await host.ScalarAsync<string>("SELECT state FROM circuit_breakers WHERE name = 'sap'")).ShouldBe("closed");
        (await host.ScalarAsync<int>("SELECT open_count FROM circuit_breakers WHERE name = 'sap'")).ShouldBe(0);
        (await host.ScalarAsync<string>("SELECT action || '|' || target || '|' || outcome FROM admin_audit")).ShouldBe("circuit.close|sap|closed");
    }

    [Fact]
    public async Task Cerrar_un_circuito_que_ya_estaba_cerrado_responde_200_y_se_audita_como_tal()
    {
        await using var host = await StartAsync(open: false);

        var response = await host.Client.PostAsync("/admin/circuit-breakers/sap/close", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ScalarAsync<string>("SELECT outcome FROM admin_audit")).ShouldBe("already_closed");
    }

    [Fact]
    public async Task Cerrar_un_circuito_que_no_existe_responde_404()
    {
        await using var host = await StartAsync(open: false);

        (await host.Client.PostAsync("/admin/circuit-breakers/otro/close", content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(0);
    }

    [Fact]
    public async Task Una_clave_de_lectura_no_puede_cerrar_el_circuito()
    {
        await using var host = await StartAsync(open: true);
        using var reader = host.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadApiKey);

        (await reader.PostAsync("/admin/circuit-breakers/sap/close", content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await host.ScalarAsync<string>("SELECT state FROM circuit_breakers WHERE name = 'sap'")).ShouldBe("open");
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(0);
    }

    [Fact]
    public async Task Sin_clave_el_circuito_responde_401()
    {
        await using var host = await StartAsync(open: true);
        using var anonymous = host.CreateAnonymousClient();

        (await anonymous.GetAsync("/admin/circuit-breakers")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/admin/circuit-breakers/sap/close", content: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Log / Seq ---

    [Fact]
    public async Task Cada_transicion_deja_un_evento_estructurado_para_Seq_con_el_estado_el_motivo_y_la_proxima_prueba()
    {
        var sink = new CollectingSink();
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)),
            sap: sap);
        await sap.SetFaultsAsync(new { Outage = true });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var gateway = scope.ServiceProvider.GetRequiredService<Application.Sap.ISapGateway>();
            for (var n = 0; n < 5; n++)
            {
                await gateway.GetItemAsync("A00001", CancellationToken.None);
            }
        }

        await host.Client.PostAsync("/admin/circuit-breakers/sap/close", content: null);

        var opened = sink.Events.Single(logEvent => logEvent.Properties.TryGetValue("ToState", out var to) && to.ToString() == "Open");
        opened.Level.ShouldBe(LogEventLevel.Warning);
        opened.Properties["CircuitName"].ToString().ShouldBe("\"sap\"");
        opened.Properties["Reason"].ToString().ShouldBe("TransientFailures");
        opened.Properties.ContainsKey("NextProbeAt").ShouldBeTrue();
        var closed = sink.Events.Single(logEvent => logEvent.Properties.TryGetValue("ToState", out var to) && to.ToString() == "Closed");
        closed.Level.ShouldBe(LogEventLevel.Information);
    }
}
