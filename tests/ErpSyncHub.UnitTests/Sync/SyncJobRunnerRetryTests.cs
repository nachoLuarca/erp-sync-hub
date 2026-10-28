using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute.ExceptionExtensions;

namespace ErpSyncHub.UnitTests.Sync;

/// <summary>Qué hace el runner con un job que falla: reintentar con backoff, dead letter o fallo definitivo.</summary>
public class SyncJobRunnerRetryTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly FakeSapGateway _sap = new();
    private readonly FakeLogger<SyncJobRunner> _logger = new();

    public SyncJobRunnerRetryTests()
    {
        _sap.AddItem("A00001");
        _sap.AddItem("A00010");
        _queue.FailAsync(Arg.Any<ClaimedJob>(), Arg.Any<ResultError>(), Arg.Any<RetryDecision>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _queue.CompleteAsync(Arg.Any<ClaimedJob>(), Arg.Any<SapOrderReference>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
    }

    [Fact]
    public async Task Un_fallo_transitorio_programa_el_reintento_con_la_espera_del_intento()
    {
        _sap.FailOn(nameof(FakeSapGateway.FindOrderByReferenceAsync), ResultError.Transient(SapErrors.Unreachable, "Sin conexión."));
        var claim = Claim(cycleAttempt: 3);

        await Runner().RunAsync(claim, JobTimeout, CancellationToken.None);

        // Con azar 1.0 no hay recorte: 30 s × 2^(3-1) = 2 minutos.
        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == SapErrors.Unreachable),
            new RetryDecision(RetryAction.RetryLater, TimeSpan.FromMinutes(2)),
            Arg.Any<CancellationToken>());
        Messages(LogLevel.Warning).ShouldContain(message => message.Contains("Intento 3 de 12", StringComparison.Ordinal) && message.Contains("Se reintentará en", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Al_agotar_los_intentos_el_job_pasa_al_dead_letter_y_se_registra_con_nivel_Error()
    {
        _sap.FailOn(nameof(FakeSapGateway.FindOrderByReferenceAsync), ResultError.Transient(SapErrors.Unreachable, "Sin conexión."));
        var claim = Claim(cycleAttempt: 4);

        await Runner(maxAttempts: 4).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(
            claim,
            Arg.Any<ResultError>(),
            new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
        Messages(LogLevel.Error).ShouldContain(message => message.Contains("dead letter", StringComparison.Ordinal) && message.Contains("4 intentos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_error_definitivo_no_se_reintenta_ni_siquiera_en_el_primer_intento()
    {
        var claim = Claim(Fixtures.Read("woocommerce/order-processing.json").Replace("\"sku\": \"A00001\"", "\"sku\": \"NO-EXISTE\"", StringComparison.Ordinal));

        await Runner().RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == OrderSyncErrors.UnknownSku),
            new RetryDecision(RetryAction.Fail, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
        Messages(LogLevel.Warning).ShouldContain(message => message.Contains("No se reintentará", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_RUT_invalido_tampoco_se_reintenta()
    {
        var claim = Claim(Fixtures.Read("woocommerce/order-processing.json").Replace("18.765.432-7", "18.765.432-1", StringComparison.Ordinal));

        await Runner().RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == OrderSyncErrors.InvalidTaxId),
            Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.Fail),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Si_el_circuito_de_SAP_esta_abierto_el_job_vuelve_a_la_cola_sin_gastar_un_intento()
    {
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<SapOrderReference?>(ResultError.Transient(CircuitErrors.Open, "El circuito de SAP está abierto."))));
        _queue.DeferAsync(Arg.Any<ClaimedJob>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
        var claim = Claim();

        await Runner(sap).RunAsync(claim, JobTimeout, CancellationToken.None);

        await _queue.Received(1).DeferAsync(claim, Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default, default);
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Una_excepcion_inesperada_se_registra_con_nivel_Error_y_su_excepcion_completa_y_se_reintenta()
    {
        var sap = Substitute.For<ISapGateway>();
        var bug = new InvalidOperationException("bug en el adaptador");
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(bug);

        await Runner(sap).RunAsync(Claim(), JobTimeout, CancellationToken.None);

        var error = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Error && record.Exception is not null);
        error.Exception.ShouldBeSameAs(bug);
        await _queue.Received(1).FailAsync(
            Arg.Any<ClaimedJob>(),
            Arg.Is<ResultError>(failure => failure.Kind == ErrorKind.Unexpected),
            Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.RetryLater),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Una_excepcion_inesperada_en_el_ultimo_intento_deja_el_job_en_el_dead_letter_a_la_vista()
    {
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bug"));

        await Runner(sap, maxAttempts: 2).RunAsync(Claim(cycleAttempt: 2), JobTimeout, CancellationToken.None);

        await _queue.Received(1).FailAsync(
            Arg.Any<ClaimedJob>(),
            Arg.Any<ResultError>(),
            Arg.Is<RetryDecision>(decision => decision.Action == RetryAction.DeadLetter),
            Arg.Any<CancellationToken>());
        _logger.Collector.GetSnapshot().Count(record => record.Level == LogLevel.Error).ShouldBe(2); // la excepción y el dead letter
    }

    [Fact]
    public async Task Una_falla_que_no_es_cancelacion_mientras_el_servicio_se_apaga_devuelve_el_job_a_la_cola_sin_contarla_como_inesperada()
    {
        using var stopping = new CancellationTokenSource();
        var sap = Substitute.For<ISapGateway>();
        sap.FindOrderByReferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<Result<SapOrderReference?>>>(async _ =>
        {
            await stopping.CancelAsync();
            throw new ObjectDisposedException("IServiceProvider");
        });
        _queue.ReleaseAsync(Arg.Any<ClaimedJob>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
        var claim = Claim();

        await Runner(sap).RunAsync(claim, JobTimeout, stopping.Token);

        await _queue.Received(1).ReleaseAsync(claim, Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default, default);
        _logger.Collector.GetSnapshot().ShouldNotContain(record => record.Level == LogLevel.Error);
        Messages(LogLevel.Warning).ShouldContain(message => message.Contains("se apagaba", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_job_que_ya_agoto_sus_intentos_sin_cerrarse_va_al_dead_letter_sin_procesarlo()
    {
        var claim = Claim(cycleAttempt: 5);

        await Runner(maxAttempts: 4).RunAsync(claim, JobTimeout, CancellationToken.None);

        _sap.Calls.ShouldBeEmpty();
        await _queue.Received(1).FailAsync(
            claim,
            Arg.Is<ResultError>(error => error.Code == JobErrors.AttemptsExhausted && error.Message.Contains("4 intentos", StringComparison.Ordinal)),
            new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
        await _queue.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task El_ultimo_intento_permitido_si_se_procesa()
    {
        var claim = Claim(cycleAttempt: 4);

        await Runner(maxAttempts: 4).RunAsync(claim, JobTimeout, CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem();
        await _queue.ReceivedWithAnyArgs(1).CompleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Un_job_que_se_completa_no_consulta_la_politica_de_reintentos()
    {
        await Runner().RunAsync(Claim(), JobTimeout, CancellationToken.None);

        await _queue.DidNotReceiveWithAnyArgs().FailAsync(default!, default!, default, default);
    }

    private SyncJobRunner Runner(ISapGateway? sap = null, int maxAttempts = 12) =>
        new(
            new OrderSyncService(sap ?? _sap, new FakeMappingStore(), new OrderSyncOptions(), new FakeLogger<OrderSyncService>()),
            _queue,
            new RetryPolicy(new JobRetryOptions { MaxAttempts = maxAttempts }, () => 1.0),
            new ExternalMessageSanitizer(new ExternalMessageOptions()),
            _logger);

    private IEnumerable<string> Messages(LogLevel level) =>
        _logger.Collector.GetSnapshot().Where(record => record.Level == level).Select(record => record.Message);

    private static ClaimedJob Claim(string? payload = null, int cycleAttempt = 1) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            cycleAttempt,
            "worker-1",
            SyncSources.WooCommerce,
            "5501",
            SyncJobTypes.Order,
            payload ?? Fixtures.Read("woocommerce/order-processing.json"),
            100013.55m,
            cycleAttempt);
}
