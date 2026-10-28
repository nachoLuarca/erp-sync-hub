using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace ErpSyncHub.IntegrationTests.Resilience;

/// <summary>El circuit breaker delante de SAP: cuándo se abre, qué cuenta como falla y que un circuito abierto no llama a SAP.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SapCircuitBreakerTests(PostgresFixture postgres) : IDisposable
{
    private readonly SapMockHost _sap = new();

    public void Dispose() => _sap.Dispose();

    private Task<WebhookHost> StartAsync(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        WebhookHost.StartAsync(postgres, configure, sap: _sap);

    private static async Task<Result<SapItem?>> GetItemAsync(WebhookHost host, string itemCode = "A00001")
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISapGateway>().GetItemAsync(itemCode, CancellationToken.None);
    }

    private static Task<string?> StateAsync(WebhookHost host) => host.ScalarAsync<string>("SELECT state FROM circuit_breakers WHERE name = 'sap'");

    [Fact]
    public async Task Cinco_fallas_pasajeras_seguidas_abren_el_circuito_y_la_sexta_llamada_ni_siquiera_llega_a_SAP()
    {
        await using var host = await StartAsync();
        await _sap.SetFaultsAsync(new { Outage = true });

        for (var n = 0; n < 4; n++)
        {
            (await GetItemAsync(host)).Error!.Kind.ShouldBe(ErrorKind.Transient);
            (await StateAsync(host)).ShouldBe("closed");
        }

        (await GetItemAsync(host)).Error!.Code.ShouldNotBe(CircuitErrors.Open); // la quinta todavía llegó a SAP
        (await StateAsync(host)).ShouldBe("open");

        // SAP se recupera, pero el circuito sigue abierto hasta la prueba: la llamada ni siquiera sale.
        await _sap.ClearFaultsAsync();
        var blocked = await GetItemAsync(host);

        blocked.IsFailure.ShouldBeTrue();
        blocked.Error!.Code.ShouldBe(CircuitErrors.Open);
        blocked.Error.IsTransient.ShouldBeTrue();
        blocked.Error.Message.ShouldContain("no se llamó a SAP");
    }

    private static byte[] OrderWithId(int id) =>
        System.Text.Encoding.UTF8.GetBytes(
            Fixtures.Read("woocommerce/order-processing.json")
                .Replace("\"id\": 5501,", $"\"id\": {id},", StringComparison.Ordinal)
                .Replace("\"number\": \"5501\"", $"\"number\": \"{id}\"", StringComparison.Ordinal));

    [Fact]
    public async Task Con_SAP_caido_el_worker_deja_de_tomar_jobs_sin_gastar_sus_intentos_y_los_completa_al_volver_SAP()
    {
        await _sap.SetFaultsAsync(new { Outage = true });
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder =>
            {
                builder.UseSetting("Worker:Concurrency", "2");
                builder.UseSetting("Worker:Retry:MaxAttempts", "100");
                builder.UseSetting("Worker:Retry:BaseDelay", "00:00:00.100");
                builder.UseSetting("Worker:Retry:MaxDelay", "00:00:00.300");
                builder.UseSetting("Circuit:FailureThreshold", "3");
                builder.UseSetting("Circuit:CooldownBase", "00:00:01");
                builder.UseSetting("Circuit:CooldownMax", "00:00:02");
            },
            sap: _sap,
            workerEnabled: true);

        for (var id = 6001; id <= 6006; id++)
        {
            (await host.PostSignedAsync(OrderWithId(id))).StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        }

        await host.WaitUntilAsync("SELECT coalesce(bool_or(state = 'open'), false) FROM circuit_breakers WHERE name = 'sap'", "que el circuito se abra");

        // Con el circuito abierto nadie toma jobs: los intentos dejan de crecer aunque las pruebas semiabiertas sigan fallando.
        await Task.Delay(500); // deja terminar los intentos que venían en vuelo
        var attemptsWhenOpen = await host.ScalarAsync<long>("SELECT sum(attempt_count) FROM sync_jobs");
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await host.ScalarAsync<long>("SELECT sum(attempt_count) FROM sync_jobs")).ShouldBe(attemptsWhenOpen);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs WHERE status = 'dead_letter'")).ShouldBe(0);
        (await host.ScalarAsync<int>("SELECT max(cycle_attempts) FROM sync_jobs")).ShouldBeLessThanOrEqualTo(4); // solo lo gastado antes de abrirse

        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT count(*) = 6 AND bool_and(status = 'succeeded') FROM sync_jobs", "que los pedidos terminen al volver SAP", TimeSpan.FromSeconds(60));

        (await StateAsync(host)).ShouldBe("closed");
        (await host.ScalarAsync<long>("SELECT count(DISTINCT sap_doc_entry) FROM sync_jobs")).ShouldBe(6);
    }

    private Task<WebhookHost> StartWithWorkerAsync(params (string Key, string Value)[] settings) =>
        WebhookHost.StartAsync(
            postgres,
            builder =>
            {
                builder.UseSetting("Worker:Concurrency", "2");
                builder.UseSetting("Worker:Retry:MaxAttempts", "100");
                builder.UseSetting("Worker:Retry:BaseDelay", "00:00:00.100");
                builder.UseSetting("Worker:Retry:MaxDelay", "00:00:00.300");
                builder.UseSetting("Circuit:FailureThreshold", "3");
                builder.UseSetting("Circuit:CooldownBase", "00:00:01");
                builder.UseSetting("Circuit:CooldownMax", "00:00:04");
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            },
            sap: _sap,
            workerEnabled: true);

    [Fact]
    public async Task Mientras_SAP_sigue_caido_las_pruebas_fallan_y_el_enfriamiento_crece_hasta_el_tope()
    {
        await _sap.SetFaultsAsync(new { Outage = true });
        await using var host = await StartWithWorkerAsync();
        (await host.PostSignedAsync(OrderWithId(6001))).StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);

        // Enfriamientos de 1, 2 y 4 segundos: la tercera apertura ya está en el tope configurado.
        await host.WaitUntilAsync("SELECT coalesce(bool_or(open_count >= 4), false) FROM circuit_breakers WHERE name = 'sap'", "que fallen varias pruebas seguidas", TimeSpan.FromSeconds(30));

        var cooldown = await host.ScalarAsync<double>("SELECT extract(epoch FROM next_probe_at - opened_at)::float8 FROM circuit_breakers WHERE name = 'sap'");
        cooldown.ShouldBe(4, tolerance: 0.5);
        (await host.ScalarAsync<string>("SELECT state FROM circuit_breakers WHERE name = 'sap'")).ShouldBeOneOf("open", "half_open");

        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el pedido termine al volver SAP", TimeSpan.FromSeconds(30));
        (await StateAsync(host)).ShouldBe("closed");
    }

    [Fact]
    public async Task Errores_intermitentes_de_SAP_no_abren_el_circuito_porque_cada_exito_reinicia_la_cuenta()
    {
        // Falla una de cada diez peticiones: nunca hay tres fallas seguidas, aunque SAP esté lejos de sano. Un solo bucle, para que las
        // peticiones de un intento no se mezclen con las de otro.
        await _sap.SetFaultsAsync(new { ErrorEveryNth = 10, ErrorStatusCode = 500 });
        await using var host = await StartWithWorkerAsync(("Worker:Concurrency", "1"));

        for (var id = 6001; id <= 6004; id++)
        {
            (await host.PostSignedAsync(OrderWithId(id))).StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        }

        await host.WaitUntilAsync("SELECT count(*) = 4 AND bool_and(status = 'succeeded') FROM sync_jobs", "que los pedidos terminen a pesar de los errores", TimeSpan.FromSeconds(60));

        (await host.ScalarAsync<long>("SELECT count(*) FROM circuit_breakers WHERE state <> 'closed' OR open_count > 0")).ShouldBe(0);
    }

    [Fact]
    public async Task Un_SAP_demasiado_lento_cuenta_como_falla_y_abre_el_circuito()
    {
        await _sap.SetFaultsAsync(new { LatencyMs = 3000 });
        await using var host = await StartWithWorkerAsync(("SAP:RequestTimeout", "00:00:00.300"));
        (await host.PostSignedAsync(OrderWithId(6001))).StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);

        await host.WaitUntilAsync("SELECT coalesce(bool_or(state <> 'closed'), false) FROM circuit_breakers WHERE name = 'sap'", "que los tiempos agotados abran el circuito", TimeSpan.FromSeconds(30));

        (await host.ScalarAsync<string>("SELECT last_error_code FROM circuit_breakers WHERE name = 'sap'")).ShouldBeOneOf(SapErrors.Timeout, SapErrors.Unreachable); // según dónde lo corte el cliente HTTP (el login o la consulta)
    }

    [Fact]
    public async Task Las_credenciales_rechazadas_abren_el_circuito_con_la_primera_llamada()
    {
        await using var host = await StartAsync(builder => builder.UseSetting("SAP:Password", "contrasena-incorrecta"));

        var result = await GetItemAsync(host);

        result.Error!.Code.ShouldBe(SapErrors.InvalidCredentials);
        (await StateAsync(host)).ShouldBe("open");
        (await host.ScalarAsync<string>("SELECT reason FROM circuit_breakers")).ShouldBe("credentials_rejected");
        (await host.ScalarAsync<string>("SELECT last_error_code FROM circuit_breakers")).ShouldBe(SapErrors.InvalidCredentials);
    }

    [Fact]
    public async Task Un_rechazo_de_negocio_de_SAP_prueba_que_responde_y_no_cuenta_como_falla()
    {
        await using var host = await StartAsync();
        await _sap.SetFaultsAsync(new { Outage = true });
        for (var n = 0; n < 4; n++)
        {
            await GetItemAsync(host);
        }

        (await host.ScalarAsync<int>("SELECT consecutive_failures FROM circuit_breakers")).ShouldBe(4);
        await _sap.ClearFaultsAsync();

        // SAP vuelve y rechaza una orden por un dato (cliente inexistente): es una respuesta, no una falla de infraestructura.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var order = new NewSapOrder("NO-EXISTE", "WC-1", null, null, [new NewSapOrderLine("A00001", 1, 100)]);
            var rejected = await scope.ServiceProvider.GetRequiredService<ISapGateway>().CreateOrderAsync(order, CancellationToken.None);
            rejected.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        }

        (await host.ScalarAsync<int>("SELECT consecutive_failures FROM circuit_breakers")).ShouldBe(0);
        (await StateAsync(host)).ShouldBe("closed");
    }

    [Fact]
    public async Task Muchos_rechazos_de_negocio_nunca_abren_el_circuito()
    {
        await using var host = await StartAsync();

        for (var n = 0; n < 12; n++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var order = new NewSapOrder("NO-EXISTE", $"WC-{n}", null, null, [new NewSapOrderLine("A00001", 1, 100)]);
            (await scope.ServiceProvider.GetRequiredService<ISapGateway>().CreateOrderAsync(order, CancellationToken.None)).Error!.Kind.ShouldBe(ErrorKind.Rejected);
        }

        (await host.ScalarAsync<long>("SELECT count(*) FROM circuit_breakers WHERE state <> 'closed'")).ShouldBe(0);
    }

    [Fact]
    public async Task Un_exito_entre_las_fallas_reinicia_la_cuenta()
    {
        await using var host = await StartAsync();

        for (var round = 0; round < 3; round++)
        {
            await _sap.SetFaultsAsync(new { Outage = true });
            for (var n = 0; n < 4; n++)
            {
                await GetItemAsync(host);
            }

            await _sap.ClearFaultsAsync();
            (await GetItemAsync(host)).IsSuccess.ShouldBeTrue();
        }

        (await StateAsync(host)).ShouldBe("closed");
        (await host.ScalarAsync<int>("SELECT consecutive_failures FROM circuit_breakers")).ShouldBe(0);
    }

    [Fact]
    public async Task Una_llamada_cancelada_no_cuenta_como_falla()
    {
        await using var host = await StartAsync();
        await _sap.SetFaultsAsync(new { LatencyMs = 5000 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await using var scope = host.Services.CreateAsyncScope();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<ISapGateway>().GetItemAsync("A00001", cts.Token));

        (await host.ScalarAsync<long>("SELECT count(*) FROM circuit_breakers WHERE consecutive_failures > 0")).ShouldBe(0);
    }

    [Fact]
    public async Task Deshabilitado_por_configuracion_las_llamadas_siguen_yendo_a_SAP_y_nunca_se_abre()
    {
        await using var host = await StartAsync(builder => builder.UseSetting("Circuit:Enabled", "false"));
        await _sap.SetFaultsAsync(new { Outage = true });

        for (var n = 0; n < 10; n++)
        {
            (await GetItemAsync(host)).Error!.Code.ShouldNotBe(CircuitErrors.Open);
        }

        await _sap.ClearFaultsAsync();
        (await GetItemAsync(host)).IsSuccess.ShouldBeTrue();
        (await host.ScalarAsync<long>("SELECT count(*) FROM circuit_breakers")).ShouldBe(0);
    }
}
