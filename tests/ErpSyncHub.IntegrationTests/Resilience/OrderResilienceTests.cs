using System.Net;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace ErpSyncHub.IntegrationTests.Resilience;

/// <summary>
/// Un pedido sobrevive a una caída o a una degradación de SAP: el webhook se responde igual, el job se reintenta con backoff y,
/// cuando SAP vuelve, el pedido se completa sin duplicar la orden ni el cliente. SAP es el simulado, con fallas inyectadas por
/// <c>PUT /_mock/faults</c>; el hub es la Api completa sobre PostgreSQL real.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderResilienceTests(PostgresFixture postgres) : IDisposable
{
    private const string Rut = "18.765.432-7";

    private static readonly string[] TimeoutCodes = ["sap.timeout", "sap.unreachable", "sap.session"];

    private static readonly string[] OutageCodes = ["sap.session", "sap.unreachable", "sap.server_error", "sap.timeout"];

    private readonly SapMockHost _sap = new();

    public void Dispose() => _sap.Dispose();

    private static byte[] Order => Fixtures.ReadBytes("woocommerce/order-processing.json");

    private static byte[] OrderWithId(int id) =>
        System.Text.Encoding.UTF8.GetBytes(
            Fixtures.Read("woocommerce/order-processing.json")
                .Replace("\"id\": 5501,", $"\"id\": {id},", StringComparison.Ordinal)
                .Replace("\"number\": \"5501\"", $"\"number\": \"{id}\"", StringComparison.Ordinal));

    /// <summary>Reintentos rápidos (decenas de milisegundos) para que la prueba no espere los 30 segundos de la espera base.</summary>
    private static Action<IWebHostBuilder> FastRetries(
        int maxAttempts = 80,
        Action<IWebHostBuilder>? more = null,
        string baseDelay = "00:00:00.050",
        string maxDelay = "00:00:00.150") =>
        builder =>
        {
            builder.UseSetting("Worker:Retry:MaxAttempts", maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Worker:Retry:BaseDelay", baseDelay);
            builder.UseSetting("Worker:Retry:MaxDelay", maxDelay);
            more?.Invoke(builder);
        };

    private Task<WebhookHost> StartAsync(Action<IWebHostBuilder>? configure = null) =>
        WebhookHost.StartAsync(postgres, configure ?? FastRetries(), sap: _sap, workerEnabled: true);

    private async Task<int> CountAsync(string pathAndQuery)
    {
        using var document = await _sap.GetAsync(pathAndQuery);
        return document.RootElement.GetProperty("value").GetArrayLength();
    }

    private Task<int> OrdersAsync() => CountAsync("Orders?$select=DocEntry");

    private Task<int> CustomersWithTaxIdAsync() =>
        CountAsync($"BusinessPartners?$select=CardCode&$filter={Uri.EscapeDataString($"FederalTaxID eq '{Rut}'")}");

    private static Task<long> JobsAsync(WebhookHost host, string status) =>
        host.ScalarAsync<long>($"SELECT count(*) FROM sync_jobs WHERE status = '{status}'");

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("ConnectionReset")]
    public async Task Un_pedido_sobrevive_a_una_caida_total_de_SAP_y_se_completa_al_volver_sin_duplicar_la_orden(string outageMode)
    {
        await _sap.SetFaultsAsync(new { Outage = true, OutageMode = outageMode });
        await using var host = await StartAsync();

        var webhook = await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(attempt_count >= 4) FROM sync_jobs", "que el job se reintente varias veces con SAP caído");

        // Mientras SAP está caído la tienda recibe su 202, el job espera y el hub sigue vivo (SAP caído solo degrada).
        webhook.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBeOneOf("retrying", "processing");
        OutageCodes.ShouldContain((await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs"))!);
        (await host.ScalarAsync<DateTimeOffset?>("SELECT finished_at FROM sync_jobs")).ShouldBeNull();
        var health = await host.Client.GetAsync("/health");
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).ShouldContain("Degraded");

        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine al volver SAP");

        (await OrdersAsync()).ShouldBe(1);
        (await CustomersWithTaxIdAsync()).ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts WHERE outcome = 'failed'")).ShouldBeGreaterThanOrEqualTo(3);
        (await host.ScalarAsync<string>("SELECT outcome FROM sync_attempts ORDER BY number DESC LIMIT 1")).ShouldBe("succeeded");
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBeNull();
    }

    [Theory]
    [InlineData(4)] // el cliente se crea y la creación de la orden falla: el reintento no duplica el cliente
    [InlineData(5)] // falla a mitad de camino, después de resolver los productos
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task Con_errores_5xx_intermitentes_el_pedido_se_completa_reanudando_sin_duplicar_nada(int errorEveryNth)
    {
        await _sap.SetFaultsAsync(new { ErrorEveryNth = errorEveryNth, ErrorStatusCode = 502 });
        await using var host = await StartAsync();

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine pese a los errores intermitentes");
        await _sap.ClearFaultsAsync();

        (await OrdersAsync()).ShouldBe(1);
        (await CustomersWithTaxIdAsync()).ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts WHERE outcome = 'succeeded'")).ShouldBe(1);
    }

    [Fact]
    public async Task Con_errores_5xx_aleatorios_el_pedido_termina_con_una_sola_orden_y_un_solo_cliente()
    {
        await _sap.SetFaultsAsync(new { ErrorRate = 0.35, ErrorStatusCode = 503 });
        await using var host = await StartAsync();

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine pese a los errores aleatorios", TimeSpan.FromSeconds(60));
        await _sap.ClearFaultsAsync();

        (await OrdersAsync()).ShouldBe(1);
        (await CustomersWithTaxIdAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Con_la_sesion_expirada_a_cada_rato_el_cliente_la_renueva_y_el_job_termina_a_la_primera()
    {
        await _sap.SetFaultsAsync(new { SessionExpiryEveryNth = 2 });
        await using var host = await StartAsync();

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");
        await _sap.ClearFaultsAsync();

        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(1);
        (await OrdersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Si_SAP_responde_mas_lento_que_el_tiempo_de_espera_el_job_se_reintenta_y_termina_al_normalizarse()
    {
        await _sap.SetFaultsAsync(new { LatencyMs = 3000 });
        await using var host = await StartAsync(FastRetries(more: builder => builder.UseSetting("SAP:RequestTimeout", "00:00:01")));

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(attempt_count >= 2) FROM sync_jobs", "que el job agote el tiempo de espera dos veces", TimeSpan.FromSeconds(45));
        var code = (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs"))!;
        var message = await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs");
        TimeoutCodes.ShouldContain(code, $"{code}: {message}");

        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine al normalizarse SAP", TimeSpan.FromSeconds(45));

        (await OrdersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Si_la_tienda_reenvia_el_webhook_durante_la_caida_sigue_habiendo_un_solo_job_y_una_sola_orden()
    {
        await _sap.SetFaultsAsync(new { Outage = true });
        await using var host = await StartAsync();

        var first = await host.PostSignedAsync(Order);
        var second = await host.PostSignedAsync(Order);
        var third = await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(attempt_count >= 2) FROM sync_jobs", "que el job se reintente");
        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        new[] { first, second, third }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Accepted);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs")).ShouldBe(1);
        (await OrdersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Si_la_orden_se_creo_en_SAP_pero_el_job_no_llego_a_cerrarse_el_reintento_no_la_duplica()
    {
        // Lo que queda si el hub cae justo después de crear la orden: la orden está en SAP y el job sigue pendiente.
        await _sap.PostAsync(
            "Orders",
            new { CardCode = "C00001", NumAtCard = "WC-5501", DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 2, UnitPrice = 35991 } } });
        await using var host = await StartAsync();

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        (await OrdersAsync()).ShouldBe(1);
        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await CustomersWithTaxIdAsync()).ShouldBe(0); // no hizo falta crear al cliente: la orden ya existía
    }

    [Fact]
    public async Task Doce_pedidos_durante_una_caida_se_completan_al_volver_cada_uno_con_su_orden_y_con_un_solo_cliente()
    {
        await _sap.SetFaultsAsync(new { Outage = true });
        // Con doce jobs reintentando a la vez, el presupuesto de intentos debe ser holgado frente a la duración de la caída.
        await using var host = await StartAsync(
            FastRetries(maxAttempts: 100, more: builder => builder.UseSetting("Worker:Concurrency", "4"), baseDelay: "00:00:00.200", maxDelay: "00:00:00.600"));

        for (var id = 6001; id <= 6012; id++)
        {
            (await host.PostSignedAsync(OrderWithId(id))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await host.WaitUntilAsync("SELECT count(*) = 12 AND bool_and(attempt_count >= 1) FROM sync_jobs", "que todos los pedidos se hayan intentado");
        (await JobsAsync(host, "succeeded")).ShouldBe(0);

        await _sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT count(*) = 12 AND bool_and(status = 'succeeded') FROM sync_jobs", "que todos los pedidos terminen al volver SAP", TimeSpan.FromSeconds(60));

        (await OrdersAsync()).ShouldBe(12);
        (await CustomersWithTaxIdAsync()).ShouldBe(1);
        using var orders = await _sap.GetAsync("Orders?$select=NumAtCard");
        orders.RootElement.GetProperty("value").EnumerateArray().Select(order => order.GetProperty("NumAtCard").GetString()).Distinct().Count().ShouldBe(12);
        (await host.ScalarAsync<long>("SELECT count(DISTINCT sap_doc_entry) FROM sync_jobs")).ShouldBe(12);
    }

    [Fact]
    public async Task Si_la_caida_dura_mas_que_los_intentos_el_job_queda_en_el_dead_letter_y_se_completa_con_un_reintento_manual()
    {
        await _sap.SetFaultsAsync(new { Outage = true });
        await using var host = await StartAsync(FastRetries(maxAttempts: 3));

        await host.PostSignedAsync(Order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'dead_letter') FROM sync_jobs", "que el job agote sus intentos");
        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(3);

        await _sap.ClearFaultsAsync();
        using var listed = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/jobs?status=dead_letter"));
        var id = listed.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await host.Client.PostAsync($"/admin/jobs/{id}/retry", content: null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el reintento manual termine el job");

        (await OrdersAsync()).ShouldBe(1);
        (await CustomersWithTaxIdAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Un_error_definitivo_no_se_reintenta_aunque_SAP_este_degradado()
    {
        await _sap.SetFaultsAsync(new { LatencyMs = 50 });
        await using var host = await StartAsync();
        var unknownSku = System.Text.Encoding.UTF8.GetBytes(
            Fixtures.Read("woocommerce/order-processing.json").Replace("\"sku\": \"A00001\"", "\"sku\": \"NO-EXISTE\"", StringComparison.Ordinal));

        await host.PostSignedAsync(unknownSku);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle de forma definitiva");
        await Task.Delay(500); // con reintentos de 50 ms habría tiempo de sobra para ver intentos de más

        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.unknown_sku");
        await _sap.ClearFaultsAsync();
        (await OrdersAsync()).ShouldBe(0);
    }
}
