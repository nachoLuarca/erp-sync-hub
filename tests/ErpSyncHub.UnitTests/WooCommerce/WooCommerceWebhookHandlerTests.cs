using System.Text;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.UnitTests.WooCommerce;

public class WooCommerceWebhookHandlerTests
{
    private const string Secret = "secreto-de-pruebas-del-webhook-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 15, 4, 10, TimeSpan.Zero);

    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly FakeTimeProvider _time = new(Now);
    private SyncJob? _enqueued;

    public WooCommerceWebhookHandlerTests()
    {
        _queue.EnqueueAsync(Arg.Do<SyncJob>(job => _enqueued = job), Arg.Any<CancellationToken>())
            .Returns(call => new EnqueueResult(call.Arg<SyncJob>().Id, Created: true));
    }

    private static byte[] ProcessingOrder => Fixtures.ReadBytes("woocommerce/order-processing.json");

    [Fact]
    public async Task Un_pedido_pagado_y_firmado_se_encola_con_sus_datos()
    {
        var body = ProcessingOrder;

        var result = await HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(WebhookOutcome.Accepted);
        result.Value.JobId.ShouldBe(_enqueued!.Id);
        _enqueued.Source.ShouldBe("woocommerce");
        _enqueued.ExternalId.ShouldBe("5501");
        _enqueued.Type.ShouldBe("order");
        _enqueued.StoreTotal.ShouldBe(100013.55m);
        _enqueued.Status.ShouldBe(JobStatus.Pending);
        _enqueued.CreatedAt.ShouldBe(Now);
        _enqueued.Payload.ShouldBe(Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task El_payload_guardado_es_el_cuerpo_exacto_que_se_recibio()
    {
        var body = Encoding.UTF8.GetBytes("""{ "id": 7,   "status": "completed", "line_items": [], "total": "10", "billing": {"last_name": "Muñoz"} }""");

        await HandlerWith().HandleAsync(body, Sign(body), null, CancellationToken.None);

        _enqueued!.Payload.ShouldBe(Encoding.UTF8.GetString(body));
        _enqueued.Payload.ShouldContain("Muñoz");
    }

    [Theory]
    [InlineData("order.created")]
    [InlineData("order.updated")]
    [InlineData("ORDER.UPDATED")]
    [InlineData(" order.created ")]
    [InlineData(null)]
    public async Task Los_topics_de_pedido_se_aceptan(string? topic)
    {
        var body = ProcessingOrder;

        var result = await HandlerWith().HandleAsync(body, Sign(body), topic, CancellationToken.None);

        result.Value.Outcome.ShouldBe(WebhookOutcome.Accepted);
    }

    [Theory]
    [InlineData("order.deleted")]
    [InlineData("order.restored")]
    [InlineData("customer.created")]
    [InlineData("product.updated")]
    public async Task Los_demas_topics_se_ignoran_sin_encolar_ni_interpretar_el_cuerpo(string topic)
    {
        var body = Encoding.UTF8.GetBytes("""{"id":5501}""");

        var result = await HandlerWith().HandleAsync(body, Sign(body), topic, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(WebhookOutcome.IgnoredTopic);
        result.Value.Detail.ShouldBe($"topic:{topic}");
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("completed")]
    [InlineData("Completed")]
    public async Task Los_pedidos_pagados_se_encolan(string status)
    {
        var body = OrderWithStatus(status);

        var result = await HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None);

        result.Value.Outcome.ShouldBe(WebhookOutcome.Accepted);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("on-hold")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    [InlineData("failed")]
    [InlineData("trash")]
    [InlineData("checkout-draft")]
    public async Task Los_pedidos_sin_pagar_o_cerrados_se_ignoran_sin_encolar(string status)
    {
        var body = OrderWithStatus(status);

        var result = await HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(WebhookOutcome.IgnoredStatus);
        result.Value.Detail.ShouldBe($"status:{status}");
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task Los_estados_aceptados_se_pueden_cambiar_por_configuracion()
    {
        var options = new WooCommerceWebhookOptions { Secret = Secret, AcceptedStatuses = ["On-Hold", " completed "] };
        var handler = new WooCommerceWebhookHandler(_queue, _time, options);
        var onHold = OrderWithStatus("on-hold");
        var processing = OrderWithStatus("processing");

        (await handler.HandleAsync(onHold, Sign(onHold), null, CancellationToken.None)).Value.Outcome.ShouldBe(WebhookOutcome.Accepted);
        (await handler.HandleAsync(processing, Sign(processing), null, CancellationToken.None)).Value.Outcome.ShouldBe(WebhookOutcome.IgnoredStatus);
    }

    [Fact]
    public async Task Un_pedido_ya_recibido_se_informa_como_duplicado_con_el_job_existente()
    {
        var existing = Guid.CreateVersion7();
        _queue.EnqueueAsync(Arg.Any<SyncJob>(), Arg.Any<CancellationToken>()).Returns(new EnqueueResult(existing, Created: false));
        var body = ProcessingOrder;

        var result = await HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(WebhookOutcome.Duplicate);
        result.Value.JobId.ShouldBe(existing);
    }

    [Fact]
    public async Task El_ping_se_responde_sin_firma_y_sin_encolar()
    {
        var ping = Encoding.UTF8.GetBytes("webhook_id=42");

        var result = await HandlerWith().HandleAsync(ping, signature: null, topic: null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(WebhookOutcome.Pong);
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("no-es-base64")]
    public async Task Una_firma_ausente_o_incorrecta_se_rechaza_sin_encolar(string? signature)
    {
        var result = await HandlerWith().HandleAsync(ProcessingOrder, signature, "order.updated", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(WebhookErrors.InvalidSignature);
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task La_firma_se_verifica_antes_que_el_topic_y_antes_de_leer_el_cuerpo()
    {
        var garbage = Encoding.UTF8.GetBytes("esto no es json");

        var invalidSignature = await HandlerWith().HandleAsync(garbage, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "order.deleted", CancellationToken.None);
        var validSignature = await HandlerWith().HandleAsync(garbage, Sign(garbage), "order.updated", CancellationToken.None);

        invalidSignature.Error!.Code.ShouldBe(WebhookErrors.InvalidSignature);
        validSignature.Error!.Code.ShouldBe("woocommerce.invalid_order");
    }

    [Fact]
    public async Task Un_cuerpo_firmado_pero_que_no_es_un_pedido_se_rechaza_como_invalido()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":5501}""");

        var result = await HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("woocommerce.invalid_order");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default);
    }

    [Fact]
    public async Task Un_fallo_de_la_cola_no_se_traga_y_la_tienda_reintentara()
    {
        _queue.EnqueueAsync(Arg.Any<SyncJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EnqueueResult>(new InvalidOperationException("base de datos caída")));
        var body = ProcessingOrder;

        await Should.ThrowAsync<InvalidOperationException>(
            () => HandlerWith().HandleAsync(body, Sign(body), "order.updated", CancellationToken.None));
    }

    [Fact]
    public async Task La_cancelacion_llega_hasta_la_cola()
    {
        using var cts = new CancellationTokenSource();
        var body = ProcessingOrder;

        await HandlerWith().HandleAsync(body, Sign(body), "order.updated", cts.Token);

        await _queue.Received(1).EnqueueAsync(Arg.Any<SyncJob>(), cts.Token);
    }

    private static byte[] OrderWithStatus(string status) =>
        Encoding.UTF8.GetBytes($$"""{"id":9001,"status":"{{status}}","total":"1000","line_items":[]}""");

    private static string Sign(byte[] body) => WooCommerceSignature.Compute(Secret, body);

    private WooCommerceWebhookHandler HandlerWith() =>
        new(_queue, _time, new WooCommerceWebhookOptions { Secret = Secret });
}
