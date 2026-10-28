using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute.ExceptionExtensions;

namespace ErpSyncHub.UnitTests.Sync;

public class SyncJobRunnerTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly FakeSapGateway _sap = new();
    private readonly FakeMappingStore _mappings = new();
    private readonly FakeLogger<SyncJobRunner> _logger = new();

    public SyncJobRunnerTests()
    {
        _sap.AddItem("A00001");
        _sap.AddItem("A00010");
        _queue.CompleteAsync(Arg.Any<ClaimedJob>(), Arg.Any<SapOrderReference>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
        _queue.FailAsync(Arg.Any<ClaimedJob>(), Arg.Any<ResultError>(), Arg.Any<RetryDecision>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
        _queue.ReleaseAsync(Arg.Any<ClaimedJob>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
    }

    [Fact]
    public async Task Un_pedido_que_se_sincroniza_cierra_el_job_con_la_orden_de_SAP()
    {
        var claim = Claim();

        await Runner(_sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).CompleteAsync(claim, Arg.Is<SapOrderReference>(order => order.DocEntry == 1 && order.CardCode == "C18765432-7"), Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default, default);
        await _queue.DidNotReceiveWithAnyArgs().ReleaseAsync(default!, default);
    }

    [Fact]
    public async Task Un_rechazo_definitivo_cierra_el_job_como_fallido_con_el_mismo_error()
    {
        var claim = Claim(Fixtures.Read("woocommerce/order-processing.json").Replace("\"sku\": \"A00001\"", "\"sku\": \"NO-EXISTE\"", StringComparison.Ordinal));

        await Runner(_sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(claim, Arg.Is<ResultError>(error => error.Code == OrderSyncErrors.UnknownSku && error.Kind == ErrorKind.Rejected), Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.Fail), Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default);
        _logger.Collector.GetSnapshot().Any(record => record.Level == LogLevel.Warning && record.Message.Contains("order.unknown_sku", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task Un_fallo_pasajero_de_SAP_deja_el_job_para_reintentar_con_su_clasificacion()
    {
        var transient = ResultError.Transient(SapErrors.Unreachable, "Sin conexión.");
        _sap.FailOn(nameof(FakeSapGateway.FindOrderByReferenceAsync), transient);
        var claim = Claim();

        await Runner(_sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(claim, transient, Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.RetryLater), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Una_excepcion_inesperada_no_tumba_al_worker_y_deja_el_job_para_reintentar()
    {
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bug en el adaptador"));
        var claim = Claim();

        await Runner(sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == JobErrors.Unexpected
                && error.Kind == ErrorKind.Unexpected
                && error.Message.Contains("InvalidOperationException", StringComparison.Ordinal)
                && error.Message.Contains("bug en el adaptador", StringComparison.Ordinal)),
            Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.RetryLater),
            Arg.Any<CancellationToken>());
        _logger.Collector.GetSnapshot().Any(record => record.Level == LogLevel.Error && record.Exception is InvalidOperationException).ShouldBeTrue();
    }

    [Fact]
    public async Task Un_job_que_supera_su_tiempo_maximo_falla_como_pasajero_y_se_corta_la_llamada()
    {
        var cancelled = false;
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return Result.Success<SapOrderReference?>(null);
        });
        var claim = Claim();

        await Runner(sap).RunAsync(claim, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        cancelled.ShouldBeTrue();
        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == JobErrors.Timeout && error.Kind == ErrorKind.Transient),
            Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.RetryLater),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Si_el_servicio_se_apaga_a_mitad_del_job_este_vuelve_a_la_cola_sin_cerrarse()
    {
        using var stopping = new CancellationTokenSource();
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await stopping.CancelAsync();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Result.Success<SapOrderReference?>(null);
        });
        var claim = Claim();

        await Runner(sap).RunAsync(claim, JobTimeout, stopping.Token);

        await _queue.Received(1).ReleaseAsync(claim, Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default);
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task El_resultado_se_escribe_con_un_token_propio_aunque_el_servicio_ya_se_este_apagando()
    {
        using var stopping = new CancellationTokenSource();
        _sap.AfterCreateOrder = () => stopping.Cancel(); // el servicio se apaga justo después de crear la orden en SAP
        CancellationToken closingToken = default;
        _queue.CompleteAsync(Arg.Any<ClaimedJob>(), Arg.Any<SapOrderReference>(), Arg.Do<CancellationToken>(token => closingToken = token))
            .Returns(Task.FromResult(Result.Success()));

        await Runner(_sap).RunAsync(Claim(), JobTimeout, stopping.Token);

        stopping.IsCancellationRequested.ShouldBeTrue();
        await _queue.ReceivedWithAnyArgs(1).CompleteAsync(default!, default!, default);
        closingToken.IsCancellationRequested.ShouldBeFalse();
        closingToken.CanBeCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task Un_job_de_un_tipo_desconocido_se_rechaza_sin_llamar_a_SAP()
    {
        var claim = Claim() with { Type = "refund" };

        await Runner(_sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(claim, Arg.Is<ResultError>(error => error.Code == JobErrors.Unsupported), Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.Fail), Arg.Any<CancellationToken>());
        _sap.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_otro_worker_retomo_el_job_el_cierre_se_ignora_con_una_advertencia()
    {
        _queue.CompleteAsync(Arg.Any<ClaimedJob>(), Arg.Any<SapOrderReference>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure(ResultError.Conflict("job.lost_lease", "Lo tiene otro worker."))));

        await Runner(_sap).RunAsync(Claim(), JobTimeout, CancellationToken.None);

        _logger.Collector.GetSnapshot().Any(record => record.Level == LogLevel.Warning && record.Message.Contains("job.lost_lease", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task Si_la_base_de_datos_falla_al_cerrar_el_error_sube_al_worker_y_el_job_se_retomara_por_el_lease()
    {
        _queue.CompleteAsync(Arg.Any<ClaimedJob>(), Arg.Any<SapOrderReference>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("base de datos caída"));

        await Should.ThrowAsync<InvalidOperationException>(() => Runner(_sap).RunAsync(Claim(), JobTimeout, CancellationToken.None));
    }

    [Fact]
    public async Task Reprocesar_un_job_cuya_orden_ya_existe_en_SAP_lo_cierra_con_exito_sin_duplicar()
    {
        var runner = Runner(_sap);
        await runner.RunAsync(Claim(), JobTimeout, CancellationToken.None);

        await runner.RunAsync(Claim(), JobTimeout, CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem();
        await _queue.ReceivedWithAnyArgs(2).CompleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task El_log_del_job_lleva_su_identificador_como_contexto()
    {
        var claim = Claim();

        await Runner(_sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        var started = _logger.Collector.GetSnapshot()[0];
        var scope = started.Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>().Single().ToDictionary(pair => pair.Key, pair => pair.Value);
        scope["JobId"].ShouldBe(claim.JobId);
        scope["ExternalId"].ShouldBe("5501");
        scope["Attempt"].ShouldBe(1);
    }

    [Fact]
    public async Task Exige_el_job()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Runner(_sap).RunAsync(null!, JobTimeout, CancellationToken.None));
    }

    private SyncJobRunner Runner(ISapGateway sap) =>
        new(new OrderSyncService(sap, _mappings, new OrderSyncOptions(), new FakeLogger<OrderSyncService>()), _queue, Retry(), new ExternalMessageSanitizer(new ExternalMessageOptions()), _logger);

    private static RetryPolicy Retry(int maxAttempts = 12) =>
        new(new JobRetryOptions { MaxAttempts = maxAttempts }, () => 1.0);

    private static ClaimedJob Claim(string? payload = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "worker-1", SyncSources.WooCommerce, "5501", SyncJobTypes.Order, payload ?? Fixtures.Read("woocommerce/order-processing.json"), 100013.55m);
}
