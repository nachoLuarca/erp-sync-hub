using System.Net;
using System.Text;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace ErpSyncHub.IntegrationTests.Workers;

/// <summary>El hub completo: un webhook firmado entra, el worker lo toma y deja una orden en SAP.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class SyncWorkerTests(PostgresFixture postgres)
{
    private static byte[] ProcessingOrder => Fixtures.ReadBytes("woocommerce/order-processing.json");

    /// <summary>Reintentos rápidos para que las pruebas no esperen los 30 segundos de la espera base.</summary>
    private static void FastRetries(IWebHostBuilder builder)
    {
        builder.UseSetting("Worker:Retry:MaxAttempts", "60");
        builder.UseSetting("Worker:Retry:BaseDelay", "00:00:00.050");
        builder.UseSetting("Worker:Retry:MaxDelay", "00:00:00.200");
    }

    [Fact]
    public async Task Un_webhook_firmado_termina_como_una_orden_de_venta_en_SAP()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);

        var response = await host.PostSignedAsync(ProcessingOrder);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<int>("SELECT sap_doc_num FROM sync_jobs")).ShouldBe(1001);
        (await host.ScalarAsync<string>("SELECT sap_card_code FROM sync_jobs")).ShouldBe("C18765432-7");
        (await host.ScalarAsync<decimal>("SELECT store_total FROM sync_jobs")).ShouldBe(100013.55m);
        (await host.ScalarAsync<decimal>("SELECT sap_total FROM sync_jobs")).ShouldBe(80055m);
        (await host.ScalarAsync<string>("SELECT outcome FROM sync_attempts")).ShouldBe("succeeded");
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts")).ShouldBe(1);

        using var order = await sap.GetAsync("Orders(1)");
        order.RootElement.GetProperty("NumAtCard").GetString().ShouldBe("WC-5501");
        order.RootElement.GetProperty("CardCode").GetString().ShouldBe("C18765432-7");
        order.RootElement.GetProperty("DocDate").GetString().ShouldBe("2026-03-10");
        order.RootElement.GetProperty("DocumentLines").EnumerateArray()
            .Select(line => (line.GetProperty("ItemCode").GetString(), line.GetProperty("Quantity").GetDecimal(), line.GetProperty("UnitPrice").GetDecimal()))
            .ShouldBe([("A00001", 2m, 35991m), ("A00010", 3m, 2691m)]);

        using var customer = await sap.GetAsync("BusinessPartners('C18765432-7')");
        customer.RootElement.GetProperty("CardName").GetString().ShouldBe("Valentina Muñoz");
        customer.RootElement.GetProperty("FederalTaxID").GetString().ShouldBe("18.765.432-7");
        customer.RootElement.GetProperty("EmailAddress").GetString().ShouldBe("valentina@example.com");
    }

    [Fact]
    public async Task El_worker_guarda_los_mapeos_de_cliente_y_de_productos()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        (await host.ScalarAsync<string>("SELECT card_code FROM customer_mappings WHERE external_key = 'customer:4521'")).ShouldBe("C18765432-7");
        (await host.ScalarAsync<long>("SELECT count(*) FROM product_mappings")).ShouldBe(2);
        (await host.ScalarAsync<string>("SELECT item_code FROM product_mappings WHERE sku = 'A00001'")).ShouldBe("A00001");
    }

    [Fact]
    public async Task Reenviar_el_webhook_del_mismo_pedido_no_crea_otra_orden()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);
        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        var completed = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"status\": \"processing\"", "\"status\": \"completed\"", StringComparison.Ordinal);
        var again = await host.PostSignedAsync(completed);
        await Task.Delay(500);

        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.JobCountAsync()).ShouldBe(1);
        using var orders = await sap.GetAsync("Orders?$select=DocEntry");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Diez_pedidos_de_un_mismo_cliente_se_procesan_en_paralelo_sin_duplicar_cliente_ni_ordenes()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);

        foreach (var id in Enumerable.Range(6001, 10))
        {
            var order = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"id\": 5501,", $"\"id\": {id},", StringComparison.Ordinal);
            (await host.PostSignedAsync(order)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await host.WaitUntilAsync("SELECT count(*) = 10 AND bool_and(status = 'succeeded') FROM sync_jobs", "que los 10 jobs terminen");

        (await host.ScalarAsync<long>("SELECT count(DISTINCT sap_doc_entry) FROM sync_jobs")).ShouldBe(10);
        using var orders = await sap.GetAsync("Orders?$select=NumAtCard");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(10);
        using var customers = await sap.GetAsync("BusinessPartners?$filter=FederalTaxID eq '18.765.432-7'&$select=CardCode");
        customers.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM customer_mappings")).ShouldBe(1);
    }

    [Fact]
    public async Task Si_la_orden_ya_existia_en_SAP_el_job_termina_sin_crear_otra()
    {
        using var sap = new SapMockHost();
        await sap.PostAsync("BusinessPartners", new { CardCode = "C-PREVIO", CardName = "Cliente previo" });
        await sap.PostAsync("Orders", new
        {
            CardCode = "C-PREVIO",
            NumAtCard = "WC-5501",
            DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1 } },
        });
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT sap_card_code FROM sync_jobs")).ShouldBe("C-PREVIO");
        using var orders = await sap.GetAsync("Orders?$select=DocEntry");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Un_SKU_desconocido_deja_el_job_fallido_con_su_motivo_y_sin_reintentarlo_solo()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);
        var order = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"sku\": \"A00001\"", "\"sku\": \"NO-EXISTE\"", StringComparison.Ordinal);

        await host.PostSignedAsync(order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");
        await Task.Delay(700); // un reintento automático, si existiera, ocurriría en este plazo

        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.unknown_sku");
        (await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs")).ShouldNotBeNull().ShouldContain("NO-EXISTE");
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT outcome FROM sync_attempts")).ShouldBe("failed");
        (await host.ScalarAsync<string>("SELECT error_kind FROM sync_attempts")).ShouldBe("rejected");
        using var orders = await sap.GetAsync("Orders?$select=DocEntry");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
        using var customers = await sap.GetAsync("BusinessPartners?$filter=CardCode eq 'C18765432-7'&$select=CardCode");
        customers.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Un_RUT_invalido_deja_el_job_fallido_sin_tocar_SAP()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);
        var order = Encoding.UTF8.GetString(ProcessingOrder).Replace("18.765.432-7", "18.765.432-1", StringComparison.Ordinal);

        await host.PostSignedAsync(order);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");

        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.invalid_tax_id");
        using var customers = await sap.GetAsync("BusinessPartners?$select=CardCode");
        customers.RootElement.GetProperty("value").GetArrayLength().ShouldBe(7); // solo los de la semilla
    }

    [Fact]
    public async Task Un_rechazo_de_SAP_deja_el_job_fallido_con_el_mensaje_de_SAP()
    {
        using var sap = new SapMockHost();
        await sap.PostAsync("BusinessPartners", new { CardCode = "C18765432-7", CardName = "Valentina", FederalTaxID = "18.765.432-7", Frozen = "tYES" });
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'failed') FROM sync_jobs", "que el job falle");

        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("order.inactive_customer");
    }

    [Fact]
    public async Task Si_SAP_no_responde_el_job_queda_esperando_un_reintento_con_un_error_pasajero()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            sapHandler: () => new FailingHandler(new HttpRequestException("Connection refused")),
            workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'retrying') FROM sync_jobs", "que el job quede esperando un reintento");

        (await host.ScalarAsync<bool>("SELECT next_attempt_at > updated_at FROM sync_jobs")).ShouldBeTrue();
        (await host.ScalarAsync<DateTimeOffset?>("SELECT finished_at FROM sync_jobs")).ShouldBeNull();
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("sap.unreachable");
        (await host.ScalarAsync<string>("SELECT error_kind FROM sync_attempts")).ShouldBe("transient");
        (await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs")).ShouldNotBeNull().ShouldContain("Connection refused");
    }

    [Fact]
    public async Task Un_fallo_pasajero_se_reintenta_solo_y_el_job_termina_cuando_SAP_vuelve()
    {
        using var sap = new SapMockHost();
        await sap.SetFaultsAsync(new { Outage = true });
        await using var host = await WebhookHost.StartAsync(postgres, FastRetries, sap: sap, workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(attempt_count >= 3) FROM sync_jobs", "que el job se reintente varias veces con SAP caído");
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBeOneOf("retrying", "processing");

        await sap.ClearFaultsAsync();
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine al volver SAP");

        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBeNull();
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts WHERE outcome = 'failed'")).ShouldBeGreaterThanOrEqualTo(2);
        using var orders = await sap.GetAsync("Orders");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Con_credenciales_de_SAP_incorrectas_el_job_queda_esperando_un_reintento_con_un_error_de_credenciales()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("SAP:Password", "incorrecta"),
            sap: sap,
            workerEnabled: true);

        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'retrying') FROM sync_jobs", "que el job quede esperando un reintento");

        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBe("sap.invalid_credentials");
        (await host.ScalarAsync<string>("SELECT last_error_message FROM sync_jobs")).ShouldNotBeNull().ShouldContain("-304");
    }

    [Fact]
    public async Task Un_job_que_otro_worker_dejo_a_medias_se_retoma_y_termina()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            sap: sap,
            workerEnabled: true,
            seed: connectionString => SeedOrphanedJobAsync(connectionString, Encoding.UTF8.GetString(ProcessingOrder)));

        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que se retome y termine el job huérfano");

        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(2);
        (await host.ScalarAsync<string>("SELECT string_agg(outcome, ',' ORDER BY number) FROM sync_attempts")).ShouldBe("abandoned,succeeded");
        using var orders = await sap.GetAsync("Orders?$select=NumAtCard");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Con_el_worker_apagado_el_job_se_guarda_pero_no_se_procesa()
    {
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(postgres, sap: sap, workerEnabled: false);

        var response = await host.PostSignedAsync(ProcessingOrder);
        await Task.Delay(700);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts")).ShouldBe(0);
    }

    [Fact]
    public async Task Al_apagar_el_servicio_a_mitad_de_un_job_este_vuelve_a_la_cola()
    {
        var host = await WebhookHost.StartAsync(postgres, sapHandler: () => new HangingHandler(), workerEnabled: true);
        var connectionString = host.ConnectionString;
        await host.PostSignedAsync(ProcessingOrder);
        await host.WaitUntilAsync("SELECT bool_and(status = 'processing') FROM sync_jobs", "que un worker tome el job");

        await host.DisposeAsync(); // apagado ordenado: el host cancela el token de los workers

        (await Sql.ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await Sql.ScalarAsync<string?>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBeNull();
        (await Sql.ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("abandoned");
    }

    [Fact]
    public async Task Tras_apagarse_un_job_liberado_lo_toma_el_siguiente_arranque()
    {
        var first = await WebhookHost.StartAsync(postgres, sapHandler: () => new HangingHandler(), workerEnabled: true);
        var connectionString = first.ConnectionString;
        await first.PostSignedAsync(ProcessingOrder);
        await first.WaitUntilAsync("SELECT bool_and(status = 'processing') FROM sync_jobs", "que un worker tome el job");
        await first.DisposeAsync();

        using var sap = new SapMockHost();
        await using var second = WebhookHost.StartOnDatabase(connectionString, sap);
        await second.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el segundo arranque lo complete");

        (await second.ScalarAsync<string>("SELECT string_agg(outcome, ',' ORDER BY number) FROM sync_attempts")).ShouldBe("abandoned,succeeded");
    }

    private static async Task SeedOrphanedJobAsync(string connectionString, string payload)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var jobId = Guid.CreateVersion7();
        await using var command = new Npgsql.NpgsqlCommand(
            """
            INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, store_total, locked_by, locked_until, created_at, updated_at)
            VALUES (@id, 'woocommerce', '5501', 'order', @payload::jsonb, 'processing', 1, 100013.55, 'worker-que-murio', now() - interval '10 minutes', now() - interval '20 minutes', now() - interval '20 minutes');
            INSERT INTO sync_attempts (id, job_id, number, worker_id, started_at, outcome)
            VALUES (gen_random_uuid(), @id, 1, 'worker-que-murio', now() - interval '20 minutes', 'in_progress');
            """,
            connection);
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Un servidor de SAP que no contesta nunca: el worker queda esperando hasta que lo cancelan.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("No debería llegar aquí.");
        }
    }

    private sealed class FailingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
