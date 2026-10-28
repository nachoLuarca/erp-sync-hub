using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Jobs;

/// <summary>La cola con reintentos: espera, dead letter, fallo definitivo y reparto de lo que vence.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class JobQueueRetryTests(PostgresFixture postgres)
{
    private static readonly ResultError Transient = ResultError.Transient("sap.unreachable", "SAP no responde.");
    private static readonly ResultError Definitive = ResultError.Rejected("order.unknown_sku", "SKU sin mapeo.");
    private static readonly SapOrderReference SapOrder = new(DocEntry: 7, DocNum: 1007, CardCode: "C12345678-5", Total: 24990m);

    private async Task<(string ConnectionString, QueueHarness Queue)> NewQueueAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        return (connectionString, QueueHarness.For(connectionString));
    }

    private static Task<T?> Scalar<T>(string connectionString, string sql) => Sql.ScalarAsync<T>(connectionString, sql);

    [Fact]
    public async Task Un_fallo_transitorio_deja_el_job_esperando_hasta_su_hora()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();
        var due = queue.Time.GetUtcNow().AddSeconds(60);

        var result = await queue.FailAsync(claim, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(60)));

        result.IsSuccess.ShouldBeTrue();
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("retrying");
        (await Scalar<DateTimeOffset>(connectionString, "SELECT next_attempt_at FROM sync_jobs")).ShouldBe(due);
        (await Scalar<string>(connectionString, "SELECT last_error_code FROM sync_jobs")).ShouldBe("sap.unreachable");
        (await Scalar<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("failed");
        (await Scalar<string>(connectionString, "SELECT error_kind FROM sync_attempts")).ShouldBe("transient");
        (await Scalar<string>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBeNull();
        (await Scalar<DateTimeOffset?>(connectionString, "SELECT finished_at FROM sync_jobs")).ShouldBeNull();
    }

    [Fact]
    public async Task Un_job_en_espera_no_se_entrega_antes_de_su_hora_y_si_despues_con_otro_intento()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var first = (await queue.ClaimAsync("worker-1")).Single();
        await queue.FailAsync(first, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(60)));

        queue.Time.Advance(TimeSpan.FromSeconds(59));
        (await queue.ClaimAsync("worker-2")).ShouldBeEmpty();
        queue.Time.Advance(TimeSpan.FromSeconds(1));
        var second = (await queue.ClaimAsync("worker-2")).Single();

        second.JobId.ShouldBe(first.JobId);
        second.AttemptNumber.ShouldBe(2);
        second.CycleAttempt.ShouldBe(2);
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("processing");
        (await Scalar<DateTimeOffset?>(connectionString, "SELECT next_attempt_at FROM sync_jobs")).ShouldBeNull();
        (await Scalar<long>(connectionString, "SELECT count(*) FROM sync_attempts")).ShouldBe(2);
    }

    [Fact]
    public async Task Despues_de_reintentar_el_job_puede_completarse_y_queda_sin_error()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var first = (await queue.ClaimAsync("worker-1")).Single();
        await queue.FailAsync(first, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(30)));
        queue.Time.Advance(TimeSpan.FromSeconds(30));
        var second = (await queue.ClaimAsync("worker-1")).Single();

        var result = await queue.CompleteAsync(second, SapOrder);

        result.IsSuccess.ShouldBeTrue();
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("succeeded");
        (await Scalar<string>(connectionString, "SELECT last_error_code FROM sync_jobs")).ShouldBeNull();
        (await Scalar<string>(connectionString, "SELECT string_agg(outcome, ',' ORDER BY number) FROM sync_attempts")).ShouldBe("failed,succeeded");
    }

    [Fact]
    public async Task El_dead_letter_cierra_el_job_y_no_se_vuelve_a_entregar()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        var result = await queue.FailAsync(claim, Transient, new RetryDecision(RetryAction.DeadLetter, TimeSpan.Zero));

        result.IsSuccess.ShouldBeTrue();
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("dead_letter");
        (await Scalar<DateTimeOffset?>(connectionString, "SELECT finished_at FROM sync_jobs")).ShouldNotBeNull();
        (await Scalar<DateTimeOffset?>(connectionString, "SELECT next_attempt_at FROM sync_jobs")).ShouldBeNull();
        queue.Time.Advance(TimeSpan.FromDays(30));
        (await queue.ClaimAsync("worker-2")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_fallo_definitivo_deja_el_job_fallido_y_no_se_vuelve_a_entregar()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        await queue.FailAsync(claim, Definitive);

        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("failed");
        queue.Time.Advance(TimeSpan.FromDays(30));
        (await queue.ClaimAsync("worker-2")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Los_jobs_vencidos_se_entregan_en_el_orden_en_que_quedaron_disponibles_junto_con_los_pendientes()
    {
        var (_, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("old"));
        var old = (await queue.ClaimAsync("worker-1")).Single();
        await queue.FailAsync(old, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromMinutes(1)));
        queue.Time.Advance(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(queue.NewJob("new"));

        queue.Time.Advance(TimeSpan.FromMinutes(2));
        var claims = await queue.ClaimAsync("worker-2", maxJobs: 5);

        // "new" estaba disponible desde su creación; "old" solo desde que venció su espera de un minuto.
        claims.Select(claim => claim.ExternalId).ShouldBe(["new", "old"]);
    }

    [Fact]
    public async Task Un_job_cuyo_lease_vence_varias_veces_acumula_intentos_en_el_ciclo()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var lease = TimeSpan.FromMinutes(5);

        (await queue.ClaimAsync("worker-1", lease: lease)).Single().CycleAttempt.ShouldBe(1);
        queue.Time.Advance(lease + TimeSpan.FromSeconds(1));
        (await queue.ClaimAsync("worker-2", lease: lease)).Single().CycleAttempt.ShouldBe(2);
        queue.Time.Advance(lease + TimeSpan.FromSeconds(1));
        var third = (await queue.ClaimAsync("worker-3", lease: lease)).Single();

        third.CycleAttempt.ShouldBe(3);
        third.AttemptNumber.ShouldBe(3);
        (await Scalar<int>(connectionString, "SELECT cycle_attempts FROM sync_jobs")).ShouldBe(3);
        (await Scalar<long>(connectionString, "SELECT count(*) FROM sync_attempts WHERE outcome = 'abandoned'")).ShouldBe(2);
    }

    [Fact]
    public async Task Programar_el_reintento_de_un_job_que_otro_worker_retomo_falla_con_lost_lease()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var slow = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(1))).Single();
        queue.Time.Advance(TimeSpan.FromMinutes(2));
        await queue.ClaimAsync("worker-2");

        var late = await queue.FailAsync(slow, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(30)));

        late.Error!.Code.ShouldBe("job.lost_lease");
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("processing");
    }

    [Fact]
    public async Task Un_job_nuevo_no_queda_atras_de_jobs_que_reintentan_una_y_otra_vez()
    {
        var (_, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var retrying = (await queue.ClaimAsync("worker-1")).Single();
        await queue.FailAsync(retrying, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(10)));
        queue.Time.Advance(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(queue.NewJob("5502"));
        queue.Time.Advance(TimeSpan.FromSeconds(10));

        // El 5501 es más antiguo, pero lleva esperando su reintento menos tiempo que el 5502 su primer intento.
        var next = (await queue.ClaimAsync("worker-2")).Single();
        next.ExternalId.ShouldBe("5502");

        // El 5501 sigue vigente: cada job se atiende en cuanto le toca, ninguno queda sin atender.
        (await queue.ClaimAsync("worker-3")).Single().ExternalId.ShouldBe("5501");
    }

    [Fact]
    public async Task Diferir_un_job_lo_deja_pendiente_sin_gastar_el_intento_del_ciclo_y_con_el_intento_abandonado()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        var result = await queue.DeferAsync(claim);

        result.IsSuccess.ShouldBeTrue();
        (await Scalar<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await Scalar<int>(connectionString, "SELECT cycle_attempts FROM sync_jobs")).ShouldBe(0);
        (await Scalar<int>(connectionString, "SELECT attempt_count FROM sync_jobs")).ShouldBe(1);
        (await Scalar<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("abandoned");

        // Se puede tomar de inmediato y vuelve a ser el primer intento del ciclo.
        (await queue.ClaimAsync("worker-2")).Single().CycleAttempt.ShouldBe(1);
    }

    [Fact]
    public async Task Diferir_un_job_que_otro_worker_retomo_falla_con_lost_lease()
    {
        var (_, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var slow = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(1))).Single();
        queue.Time.Advance(TimeSpan.FromMinutes(2));
        await queue.ClaimAsync("worker-2");

        (await queue.DeferAsync(slow)).Error!.Code.ShouldBe("job.lost_lease");
    }

    [Fact]
    public async Task Una_espera_negativa_es_un_error_de_programacion()
    {
        var (_, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => queue.FailAsync(claim, Transient, new RetryDecision(RetryAction.RetryLater, TimeSpan.FromSeconds(-1))));
    }
}
