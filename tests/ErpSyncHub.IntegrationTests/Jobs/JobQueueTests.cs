using System.Collections.Concurrent;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.IntegrationTests.Support;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Jobs;

[Collection(PostgresCollectionDefinition.Name)]
public class JobQueueTests(PostgresFixture postgres)
{
    private static readonly SapOrderReference SapOrder = new(DocEntry: 7, DocNum: 1007, CardCode: "C12345678-5", Total: 24990m);

    [Fact]
    public async Task Enqueue_guarda_un_job_nuevo_como_pendiente()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        var job = queue.NewJob("5501", storeTotal: 29990m, payload: "{\"id\":5501}");

        var result = await queue.EnqueueAsync(job);

        result.Created.ShouldBeTrue();
        result.JobId.ShouldBe(job.Id);
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await ScalarAsync<string>(connectionString, "SELECT payload ->> 'id' FROM sync_jobs")).ShouldBe("5501");
        (await ScalarAsync<decimal>(connectionString, "SELECT store_total FROM sync_jobs")).ShouldBe(29990m);
    }

    [Fact]
    public async Task Enqueue_de_un_pedido_ya_recibido_devuelve_el_existente_y_no_lo_modifica()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        var first = await queue.EnqueueAsync(queue.NewJob("5501", payload: "{\"version\":1}"));

        var second = await queue.EnqueueAsync(queue.NewJob("5501", payload: "{\"version\":2}"));

        second.Created.ShouldBeFalse();
        second.JobId.ShouldBe(first.JobId);
        (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM sync_jobs")).ShouldBe(1);
        (await ScalarAsync<string>(connectionString, "SELECT payload ->> 'version' FROM sync_jobs")).ShouldBe("1");
    }

    [Fact]
    public async Task Veinte_webhooks_simultaneos_del_mismo_pedido_producen_un_solo_job()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => queue.EnqueueAsync(queue.NewJob("5501")))));

        results.Count(result => result.Created).ShouldBe(1);
        results.Select(result => result.JobId).Distinct().Count().ShouldBe(1);
        (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM sync_jobs")).ShouldBe(1);
    }

    [Fact]
    public async Task Claim_entrega_los_mas_antiguos_primero_y_respeta_el_maximo()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;
        foreach (var id in new[] { "A", "B", "C", "D" })
        {
            await queue.EnqueueAsync(queue.NewJob(id));
            queue.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var claimed = await queue.ClaimAsync("worker-1", maxJobs: 3);

        claimed.Select(job => job.ExternalId).ShouldBe(["A", "B", "C"]);
    }

    [Fact]
    public async Task Claim_deja_el_job_en_proceso_con_su_lease_y_abre_un_intento()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501", storeTotal: 29990m, payload: "{\"id\":5501}"));
        var startedAt = queue.Time.GetUtcNow();

        var claim = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(5))).ShouldHaveSingleItem();

        claim.WorkerId.ShouldBe("worker-1");
        claim.AttemptNumber.ShouldBe(1);
        claim.Source.ShouldBe("woocommerce");
        claim.ExternalId.ShouldBe("5501");
        claim.Type.ShouldBe("order");
        claim.StoreTotal.ShouldBe(29990m);
        claim.Payload.ShouldContain("5501");
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("processing");
        (await ScalarAsync<string>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBe("worker-1");
        (await ScalarAsync<DateTimeOffset>(connectionString, "SELECT locked_until FROM sync_jobs")).ShouldBe(startedAt + TimeSpan.FromMinutes(5));
        (await ScalarAsync<int>(connectionString, "SELECT attempt_count FROM sync_jobs")).ShouldBe(1);
        (await ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("in_progress");
    }

    [Fact]
    public async Task Claim_sin_jobs_devuelve_una_lista_vacia()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;

        (await queue.ClaimAsync("worker-1", maxJobs: 5)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_job_con_lease_vigente_no_se_entrega_a_otro_worker()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(5));
        queue.Time.Advance(TimeSpan.FromMinutes(4));

        (await queue.ClaimAsync("worker-2")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_job_con_el_lease_vencido_lo_retoma_otro_worker_y_el_intento_anterior_queda_abandonado()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(5));
        queue.Time.Advance(TimeSpan.FromMinutes(5));

        var claim = (await queue.ClaimAsync("worker-2")).ShouldHaveSingleItem();

        claim.AttemptNumber.ShouldBe(2);
        claim.WorkerId.ShouldBe("worker-2");
        (await ScalarAsync<string>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBe("worker-2");
        (await ScalarAsync<int>(connectionString, "SELECT attempt_count FROM sync_jobs")).ShouldBe(2);
        (await ColumnAsync(connectionString, "SELECT outcome FROM sync_attempts ORDER BY number")).ShouldBe(["abandoned", "in_progress"]);
    }

    [Fact]
    public async Task Los_jobs_terminados_no_se_vuelven_a_entregar()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;
        await queue.EnqueueAsync(queue.NewJob("ok"));
        await queue.EnqueueAsync(queue.NewJob("bad"));
        var claims = await queue.ClaimAsync("worker-1", maxJobs: 2);
        await queue.CompleteAsync(claims.Single(c => c.ExternalId == "ok"), SapOrder);
        await queue.FailAsync(claims.Single(c => c.ExternalId == "bad"), ResultError.Rejected("sap.rejected", "No."));
        queue.Time.Advance(TimeSpan.FromDays(1));

        (await queue.ClaimAsync("worker-2", maxJobs: 10)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cinco_workers_repartiendose_sesenta_jobs_toman_cada_uno_exactamente_una_vez()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        foreach (var number in Enumerable.Range(1, 60))
        {
            await queue.EnqueueAsync(queue.NewJob($"order-{number}"));
        }

        var claimedIds = new ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(1, 5).Select(worker => Task.Run(async () =>
        {
            while (true)
            {
                var batch = await queue.ClaimAsync($"worker-{worker}", maxJobs: 4);
                if (batch.Count == 0)
                {
                    return;
                }

                foreach (var claim in batch)
                {
                    claimedIds.Add(claim.JobId);
                }
            }
        })));

        claimedIds.Count.ShouldBe(60);
        claimedIds.Distinct().Count().ShouldBe(60);
        (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM sync_attempts")).ShouldBe(60);
        (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM sync_jobs WHERE attempt_count <> 1")).ShouldBe(0);
    }

    [Fact]
    public async Task SKIP_LOCKED_salta_el_job_que_otro_esta_tomando_sin_esperarlo()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        var locked = await queue.EnqueueAsync(queue.NewJob("locked"));
        queue.Time.Advance(TimeSpan.FromSeconds(1));
        var free = await queue.EnqueueAsync(queue.NewJob("free"));

        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT id FROM sync_jobs WHERE id = @id FOR UPDATE", holder, transaction))
        {
            lockCommand.Parameters.AddWithValue("id", locked.JobId);
            await lockCommand.ExecuteScalarAsync();
        }

        // Con otro worker reteniendo el primer job, el claim no se bloquea y entrega solo el segundo.
        var claimed = await queue.ClaimAsync("worker-2", maxJobs: 5).WaitAsync(TimeSpan.FromSeconds(15));

        claimed.ShouldHaveSingleItem().JobId.ShouldBe(free.JobId);

        await transaction.CommitAsync();
        (await queue.ClaimAsync("worker-3")).ShouldHaveSingleItem().JobId.ShouldBe(locked.JobId);
    }

    [Fact]
    public async Task Complete_cierra_el_job_y_el_intento_con_la_orden_de_SAP()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501", storeTotal: 29990m));
        var claim = (await queue.ClaimAsync("worker-1")).Single();
        queue.Time.Advance(TimeSpan.FromSeconds(2));

        var result = await queue.CompleteAsync(claim, SapOrder);

        result.IsSuccess.ShouldBeTrue();
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("succeeded");
        (await ScalarAsync<int>(connectionString, "SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(7);
        (await ScalarAsync<int>(connectionString, "SELECT sap_doc_num FROM sync_jobs")).ShouldBe(1007);
        (await ScalarAsync<string>(connectionString, "SELECT sap_card_code FROM sync_jobs")).ShouldBe("C12345678-5");
        (await ScalarAsync<decimal>(connectionString, "SELECT sap_total FROM sync_jobs")).ShouldBe(24990m);
        (await ScalarAsync<string?>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBeNull();
        (await ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("succeeded");
    }

    [Fact]
    public async Task Fail_cierra_el_job_como_fallido_y_guarda_el_motivo_en_el_job_y_en_el_intento()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        var result = await queue.FailAsync(claim, ResultError.Rejected("sap.rejected", "El artículo A99 no existe."));

        result.IsSuccess.ShouldBeTrue();
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("failed");
        (await ScalarAsync<string>(connectionString, "SELECT last_error_code FROM sync_jobs")).ShouldBe("sap.rejected");
        (await ScalarAsync<string>(connectionString, "SELECT last_error_message FROM sync_jobs")).ShouldBe("El artículo A99 no existe.");
        (await ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("failed");
        (await ScalarAsync<string>(connectionString, "SELECT error_kind FROM sync_attempts")).ShouldBe("rejected");
    }

    [Fact]
    public async Task Un_worker_que_perdio_el_lease_no_puede_cerrar_el_job_que_otro_retomo()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var slow = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(5))).Single();
        queue.Time.Advance(TimeSpan.FromMinutes(6));
        var fast = (await queue.ClaimAsync("worker-2")).Single();

        var lateResult = await queue.CompleteAsync(slow, SapOrder);
        var lateFailure = await queue.FailAsync(slow, ResultError.Transient("sap.unavailable", "Tarde."));

        lateResult.Error!.Code.ShouldBe("job.lost_lease");
        lateFailure.Error!.Code.ShouldBe("job.lost_lease");
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("processing");
        (await queue.CompleteAsync(fast, SapOrder)).IsSuccess.ShouldBeTrue();
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("succeeded");
    }

    [Fact]
    public async Task Release_devuelve_el_job_a_la_cola_y_abandona_el_intento()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();

        var released = await queue.ReleaseAsync(claim);

        released.IsSuccess.ShouldBeTrue();
        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await ScalarAsync<string?>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBeNull();
        (await ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("abandoned");
    }

    [Fact]
    public async Task Un_job_liberado_lo_toma_otro_worker_de_inmediato_sin_esperar_el_lease()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(30))).Single();
        await queue.ReleaseAsync(claim);

        var next = (await queue.ClaimAsync("worker-2")).ShouldHaveSingleItem();

        next.AttemptNumber.ShouldBe(2);
        (await ColumnAsync(connectionString, "SELECT outcome FROM sync_attempts ORDER BY number")).ShouldBe(["abandoned", "in_progress"]);
    }

    [Fact]
    public async Task Un_worker_que_perdio_el_lease_no_puede_liberar_el_job_que_otro_retomo()
    {
        var (connectionString, queue) = await NewQueueAsync();
        using var _ = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var slow = (await queue.ClaimAsync("worker-1", lease: TimeSpan.FromMinutes(5))).Single();
        queue.Time.Advance(TimeSpan.FromMinutes(6));
        await queue.ClaimAsync("worker-2");

        var released = await queue.ReleaseAsync(slow);

        released.Error!.Code.ShouldBe("job.lost_lease");
        (await ScalarAsync<string>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBe("worker-2");
    }

    [Fact]
    public async Task Un_job_ya_cerrado_no_se_puede_cerrar_de_nuevo()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;
        await queue.EnqueueAsync(queue.NewJob("5501"));
        var claim = (await queue.ClaimAsync("worker-1")).Single();
        await queue.CompleteAsync(claim, SapOrder);

        var again = await queue.CompleteAsync(claim, SapOrder);

        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe("job.not_processing");
    }

    [Fact]
    public async Task Cerrar_un_job_inexistente_informa_que_no_existe()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;
        var ghost = new ClaimedJob(Guid.NewGuid(), Guid.NewGuid(), 1, "worker-1", "woocommerce", "1", "order", "{}", null);

        var result = await queue.CompleteAsync(ghost, SapOrder);

        result.Error!.Code.ShouldBe("job.not_found");
        result.Error.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Claim_valida_sus_argumentos()
    {
        var (_, queue) = await NewQueueAsync();
        using var _1 = queue;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => queue.ClaimAsync("worker-1", maxJobs: 0));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => queue.ClaimAsync("worker-1", lease: TimeSpan.Zero));
    }

    private async Task<(string ConnectionString, QueueHarness Queue)> NewQueueAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        return (connectionString, QueueHarness.For(connectionString));
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return await reader.IsDBNullAsync(0) ? default : reader.GetFieldValue<T>(0);
    }

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
