using System.Net;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.IntegrationTests.Health;

[Collection(PostgresCollectionDefinition.Name)]
public class HealthTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Con_PostgreSQL_y_SAP_sanos_responde_200_Healthy_con_cada_comprobacion()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap);

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        body.RootElement.GetProperty("totalDurationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        var checks = body.RootElement.GetProperty("checks");
        checks.GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Healthy");
        checks.GetProperty("sap").GetProperty("status").GetString().ShouldBe("Healthy");
        checks.GetProperty("sap").GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Si_SAP_no_responde_el_hub_queda_Degraded_pero_sigue_respondiendo_200()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            sapHandler: () => new ThrowingHandler(new HttpRequestException("Connection refused a 10.1.2.3:50000")));

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Degraded");
        body.RootElement.GetProperty("checks").GetProperty("sap").GetProperty("status").GetString().ShouldBe("Degraded");
        body.RootElement.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Mientras_SAP_esta_caido_el_hub_sigue_aceptando_webhooks()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            sapHandler: () => new ThrowingHandler(new HttpRequestException("Connection refused")));

        var health = await host.Client.GetAsync("/health");
        var webhook = await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"));

        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        webhook.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.JobCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Si_SAP_rechaza_las_credenciales_el_motivo_se_ve_sin_exponer_secretos()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("SAP:Password", "una-clave-incorrecta"),
            sap: sap);

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldContain("credenciales");
        text.ShouldNotContain("una-clave-incorrecta");
    }

    [Fact]
    public async Task Si_PostgreSQL_no_responde_el_hub_esta_Unhealthy_con_503_sin_revelar_la_conexion()
    {
        await using var host = WebhookHost.StartWithUnreachableDatabase();

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var text = await response.Content.ReadAsStringAsync();
        using var body = System.Text.Json.JsonDocument.Parse(text);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Unhealthy");
        body.RootElement.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Unhealthy");
        text.ShouldNotContain("127.0.0.1");
        text.ShouldNotContain("inexistente");
        text.ShouldNotContain("Npgsql");
    }

    [Fact]
    public async Task El_resultado_de_SAP_se_reutiliza_unos_segundos_para_no_martillarlo()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        var handler = new CountingHandler();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
            }),
            sapHandler: () => handler);

        await host.Client.GetAsync("/health");
        await host.Client.GetAsync("/health");
        await host.Client.GetAsync("/health");
        var callsWithinCache = handler.Calls;
        time.Advance(TimeSpan.FromSeconds(16));
        await host.Client.GetAsync("/health");

        callsWithinCache.ShouldBe(1);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Las_sondas_concurrentes_comparten_una_sola_consulta_a_SAP()
    {
        var handler = new CountingHandler();
        await using var host = await WebhookHost.StartAsync(postgres, sapHandler: () => handler);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => host.Client.GetAsync("/health")));

        handler.Calls.ShouldBe(1);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    /// <summary>Cuenta cuántas veces el hub intentó hablar con SAP; responde con un fallo de red.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("sin SAP"));
        }
    }
}
