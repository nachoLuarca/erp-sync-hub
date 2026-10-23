using System.Net;
using System.Text;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Webhooks;

[Collection(PostgresCollectionDefinition.Name)]
public class WooCommerceWebhookTests(PostgresFixture postgres)
{
    private static byte[] ProcessingOrder => Fixtures.ReadBytes("woocommerce/order-processing.json");

    [Fact]
    public async Task Un_pedido_firmado_se_guarda_como_job_pendiente_y_responde_202()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(ProcessingOrder);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("accepted");
        var jobId = body.RootElement.GetProperty("jobId").GetGuid();

        (await host.JobCountAsync()).ShouldBe(1);
        (await host.ScalarAsync<Guid>("SELECT id FROM sync_jobs")).ShouldBe(jobId);
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await host.ScalarAsync<string>("SELECT source FROM sync_jobs")).ShouldBe("woocommerce");
        (await host.ScalarAsync<string>("SELECT external_id FROM sync_jobs")).ShouldBe("5501");
        (await host.ScalarAsync<string>("SELECT type FROM sync_jobs")).ShouldBe("order");
        (await host.ScalarAsync<decimal>("SELECT store_total FROM sync_jobs")).ShouldBe(100013.55m);
        (await host.ScalarAsync<string>("SELECT payload -> 'billing' ->> 'email' FROM sync_jobs")).ShouldBe("valentina@example.com");
    }

    [Fact]
    public async Task El_mismo_pedido_otra_vez_responde_202_duplicado_y_no_crea_otro_job()
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        var first = await host.PostSignedAsync(ProcessingOrder);
        using var firstBody = await WebhookHost.ReadJsonAsync(first);

        var second = await host.PostSignedAsync(ProcessingOrder);

        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var secondBody = await WebhookHost.ReadJsonAsync(second);
        secondBody.RootElement.GetProperty("status").GetString().ShouldBe("duplicate");
        secondBody.RootElement.GetProperty("jobId").GetGuid().ShouldBe(firstBody.RootElement.GetProperty("jobId").GetGuid());
        (await host.JobCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Un_pedido_que_pasa_de_processing_a_completed_no_se_procesa_dos_veces()
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        await host.PostSignedAsync(ProcessingOrder, topic: "order.created");
        var completed = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"status\": \"processing\"", "\"status\": \"completed\"", StringComparison.Ordinal);

        var response = await host.PostSignedAsync(completed, topic: "order.updated");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("duplicate");
        (await host.JobCountAsync()).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT payload ->> 'status' FROM sync_jobs")).ShouldBe("processing");
    }

    [Fact]
    public async Task Quince_entregas_simultaneas_del_mismo_pedido_dejan_un_solo_job()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => host.PostSignedAsync(ProcessingOrder)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Accepted);
        var statuses = new List<string>();
        foreach (var response in responses)
        {
            using var body = await WebhookHost.ReadJsonAsync(response);
            statuses.Add(body.RootElement.GetProperty("status").GetString()!);
        }

        statuses.Count(status => status == "accepted").ShouldBe(1);
        statuses.Count(status => status == "duplicate").ShouldBe(14);
        (await host.JobCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Un_pedido_completado_de_un_invitado_tambien_se_guarda()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-guest.json"), topic: "order.created");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.ScalarAsync<string>("SELECT external_id FROM sync_jobs")).ShouldBe("5502");
        (await host.ScalarAsync<decimal>("SELECT store_total FROM sync_jobs")).ShouldBe(2990m);
    }

    [Fact]
    public async Task Una_firma_invalida_responde_401_con_ProblemDetails_y_no_guarda_nada()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostAsync(ProcessingOrder, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var problem = await WebhookHost.ReadJsonAsync(response);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(401);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("No autorizado");
        problem.RootElement.GetProperty("type").GetString().ShouldBe("urn:erp-sync-hub:problem:unauthorized");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("firma");
        problem.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Una_solicitud_sin_cabecera_de_firma_responde_401()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostAsync(ProcessingOrder, signature: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Una_firma_hecha_con_otro_secreto_responde_401()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(ProcessingOrder, secret: "otro-secreto-que-no-es-el-del-hub-99");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Un_cuerpo_alterado_despues_de_firmarse_responde_401()
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        var signature = WooCommerceSignature.Compute(ApiFactory.WebhookSecret, ProcessingOrder);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ProcessingOrder).Replace("100013.55", "1.00", StringComparison.Ordinal));

        var response = await host.PostAsync(tampered, signature);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task La_firma_se_verifica_antes_de_interpretar_el_cuerpo()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostAsync(Encoding.UTF8.GetBytes("esto no es json"), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task El_ping_de_creacion_del_webhook_responde_200_sin_guardar_nada()
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        var ping = Encoding.UTF8.GetBytes("webhook_id=42");

        using var content = new ByteArrayContent(ping);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        var response = await host.PostAsync(content, signature: null, topic: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("pong");
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task El_ping_firmado_tambien_responde_200()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(Encoding.UTF8.GetBytes("webhook_id=42"), topic: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("on-hold")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    [InlineData("failed")]
    [InlineData("trash")]
    public async Task Un_pedido_que_no_esta_pagado_se_ignora_con_200(string status)
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        var order = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"status\": \"processing\"", $"\"status\": \"{status}\"", StringComparison.Ordinal);

        var response = await host.PostSignedAsync(order, topic: "order.created");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("ignored");
        body.RootElement.GetProperty("reason").GetString().ShouldBe($"status:{status}");
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Los_estados_aceptados_se_pueden_configurar()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("Webhooks:WooCommerce:AcceptedStatuses:0", "on-hold"));
        var order = Encoding.UTF8.GetString(ProcessingOrder).Replace("\"status\": \"processing\"", "\"status\": \"on-hold\"", StringComparison.Ordinal);

        var onHold = await host.PostSignedAsync(order);
        var processing = await host.PostSignedAsync(ProcessingOrder);

        onHold.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        processing.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.JobCountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("order.deleted")]
    [InlineData("order.restored")]
    [InlineData("customer.created")]
    public async Task Los_topics_que_no_traen_un_pedido_completo_se_ignoran_con_200(string topic)
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync("""{"id":5501}""", topic);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("reason").GetString().ShouldBe($"topic:{topic}");
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Sin_cabecera_de_topic_el_pedido_se_procesa_igual()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(ProcessingOrder, topic: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("""{"id":5501,"status":"processing"}""")]
    [InlineData("""{"status":"processing","line_items":[]}""")]
    [InlineData("[1,2,3]")]
    public async Task Un_cuerpo_firmado_pero_ilegible_responde_400_con_ProblemDetails(string body)
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.PostSignedAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var problem = await WebhookHost.ReadJsonAsync(response);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Solicitud inválida");
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Un_cuerpo_mas_grande_que_el_limite_responde_413_con_ProblemDetails()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("Webhooks:WooCommerce:MaxBodyBytes", "2048"));
        var big = new byte[4096];
        Array.Fill(big, (byte)'x');

        var response = await host.PostSignedAsync(big);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        using var problem = await WebhookHost.ReadJsonAsync(response);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Cuerpo demasiado grande");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("2048");
        (await host.JobCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task El_limite_tambien_se_aplica_cuando_el_cuerpo_llega_sin_Content_Length()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("Webhooks:WooCommerce:MaxBodyBytes", "2048"));
        var big = new byte[4096];
        Array.Fill(big, (byte)'x');
        using var content = new UnknownLengthContent(big);

        var response = await host.PostAsync(content, signature: WooCommerceSignature.Compute(ApiFactory.WebhookSecret, big));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Un_cuerpo_justo_en_el_limite_se_acepta_para_su_evaluacion()
    {
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.UseSetting("Webhooks:WooCommerce:MaxBodyBytes", "1024"));
        var exact = new byte[1024];
        Array.Fill(exact, (byte)' ');

        var response = await host.PostSignedAsync(exact);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // llegó entero y se rechazó por no ser un pedido, no por tamaño
    }

    [Fact]
    public async Task Otros_metodos_HTTP_responden_405_con_ProblemDetails()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.Client.GetAsync("/webhooks/woocommerce/orders");

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        using var problem = await WebhookHost.ReadJsonAsync(response);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Método no permitido");
    }

    [Fact]
    public async Task Una_ruta_desconocida_responde_404_con_ProblemDetails()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.Client.GetAsync("/no-existe");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var problem = await WebhookHost.ReadJsonAsync(response);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Recurso no encontrado");
    }

    [Fact]
    public async Task Si_la_base_de_datos_no_responde_el_error_es_500_sin_detalles_internos()
    {
        await using var host = WebhookHost.StartWithUnreachableDatabase();

        var response = await host.PostSignedAsync(ProcessingOrder);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync();
        using var problem = System.Text.Json.JsonDocument.Parse(text);
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Error interno");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("Intenta nuevamente");
        text.ShouldNotContain("127.0.0.1");
        text.ShouldNotContain("Npgsql");
    }

    [Fact]
    public async Task El_health_check_sigue_respondiendo()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        var response = await host.Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Un cuerpo que se envía "en trozos" (Transfer-Encoding: chunked), sin Content-Length.</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
