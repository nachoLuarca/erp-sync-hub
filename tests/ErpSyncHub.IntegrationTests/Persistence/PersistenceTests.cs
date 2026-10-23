using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Domain.Mappings;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class PersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Un_job_se_guarda_y_se_lee_con_todos_sus_datos()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var job = NewJob("5501", "{\"id\":5501,\"total\":\"29990\"}", storeTotal: 29990.50m);
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(job);
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);
        var stored = await reading.SyncJobs.SingleAsync();

        stored.Id.ShouldBe(job.Id);
        stored.Source.ShouldBe("woocommerce");
        stored.ExternalId.ShouldBe("5501");
        stored.Type.ShouldBe("order");
        stored.Status.ShouldBe(JobStatus.Pending);
        stored.StoreTotal.ShouldBe(29990.50m);
        stored.CreatedAt.ShouldBe(Now);
        stored.UpdatedAt.ShouldBe(Now);
        stored.AttemptCount.ShouldBe(0);
        stored.LockedBy.ShouldBeNull();
        stored.FinishedAt.ShouldBeNull();
    }

    [Fact]
    public async Task El_payload_se_guarda_como_jsonb_consultable()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(NewJob("5501", "{\"id\": 5501,   \"billing\": {\"email\": \"ana@example.com\"}}"));
            await context.SaveChangesAsync();
        }

        var email = await ScalarAsync<string>(connectionString, "SELECT payload -> 'billing' ->> 'email' FROM sync_jobs");

        email.ShouldBe("ana@example.com");
    }

    [Fact]
    public async Task Los_enums_se_guardan_como_texto_legible_en_snake_case()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var job = NewJob("5501");
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(job);
            var attempt = job.Claim("worker-1", Now, Lease).Value;
            attempt.Fail(ResultError.Transient("sap.unavailable", "SAP no responde."), Now);
            context.SyncAttempts.Add(attempt);
            await context.SaveChangesAsync();
        }

        (await ScalarAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldBe("processing");
        (await ScalarAsync<string>(connectionString, "SELECT outcome FROM sync_attempts")).ShouldBe("failed");
        (await ScalarAsync<string>(connectionString, "SELECT error_kind FROM sync_attempts")).ShouldBe("transient");
    }

    [Fact]
    public async Task Un_job_tomado_y_cerrado_conserva_el_estado_y_la_orden_de_SAP()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var job = NewJob("5501", storeTotal: 29990m);
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(job);
            var attempt = job.Claim("worker-1", Now, Lease).Value;
            context.SyncAttempts.Add(attempt);
            job.MarkSucceeded("worker-1", new SapOrderReference(7, 1007, "C12345678-5", 24990m), Now.AddSeconds(3));
            attempt.Succeed(Now.AddSeconds(3));
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);
        var stored = await reading.SyncJobs.SingleAsync();
        var storedAttempt = await reading.SyncAttempts.SingleAsync();

        stored.Status.ShouldBe(JobStatus.Succeeded);
        stored.SapDocEntry.ShouldBe(7);
        stored.SapDocNum.ShouldBe(1007);
        stored.SapCardCode.ShouldBe("C12345678-5");
        stored.SapTotal.ShouldBe(24990m);
        stored.HasTotalMismatch.ShouldBeTrue();
        stored.FinishedAt.ShouldBe(Now.AddSeconds(3));
        storedAttempt.Outcome.ShouldBe(AttemptOutcome.Succeeded);
        storedAttempt.JobId.ShouldBe(stored.Id);
        storedAttempt.Number.ShouldBe(1);
    }

    [Fact]
    public async Task El_mismo_origen_id_externo_y_tipo_no_se_puede_guardar_dos_veces()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var first = PostgresFixture.CreateContext(connectionString))
        {
            first.SyncJobs.Add(NewJob("5501"));
            await first.SaveChangesAsync();
        }

        await using var second = PostgresFixture.CreateContext(connectionString);
        second.SyncJobs.Add(NewJob("5501"));
        var exception = await Should.ThrowAsync<DbUpdateException>(() => second.SaveChangesAsync());

        var postgresError = exception.InnerException.ShouldBeOfType<PostgresException>();
        postgresError.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgresError.ConstraintName.ShouldBe("ux_sync_jobs_source_external_id_type");
    }

    [Fact]
    public async Task Cambiar_el_id_externo_el_tipo_o_el_origen_permite_guardar()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);

        context.SyncJobs.Add(NewJob("5501"));
        context.SyncJobs.Add(NewJob("5502"));
        context.SyncJobs.Add(SyncJob.Create(SyncSources.WooCommerce, "5501", "refund", "{}", null, Now).Value);
        context.SyncJobs.Add(SyncJob.Create("otra-tienda", "5501", SyncJobTypes.Order, "{}", null, Now).Value);
        await context.SaveChangesAsync();

        (await context.SyncJobs.CountAsync()).ShouldBe(4);
    }

    [Fact]
    public async Task La_base_rechaza_un_estado_desconocido()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var exception = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            """
            INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, created_at, updated_at)
            VALUES (gen_random_uuid(), 'woocommerce', '1', 'order', '{}', 'inventado', 0, now(), now())
            """));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_sync_jobs_status");
    }

    [Fact]
    public async Task Un_intento_exige_que_exista_su_job()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var orphan = NewJob("5501").Claim("worker-1", Now, Lease).Value;
        await using var context = PostgresFixture.CreateContext(connectionString);
        context.SyncAttempts.Add(orphan);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());

        exception.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Cada_job_tiene_un_solo_intento_por_numero()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var job = NewJob("5501");
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(job);
            context.SyncAttempts.Add(job.Claim("worker-1", Now, Lease).Value);
            await context.SaveChangesAsync();
        }

        var exception = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO sync_attempts (id, job_id, number, worker_id, started_at, outcome)
            VALUES (gen_random_uuid(), '{job.Id}', 1, 'worker-2', now(), 'in_progress')
            """));

        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        exception.ConstraintName.ShouldBe("ux_sync_attempts_job_id_number");
    }

    [Fact]
    public async Task Al_borrar_un_job_se_borran_sus_intentos()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var job = NewJob("5501");
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.SyncJobs.Add(job);
            context.SyncAttempts.Add(job.Claim("worker-1", Now, Lease).Value);
            await context.SaveChangesAsync();
        }

        await ExecuteAsync(connectionString, "DELETE FROM sync_jobs");

        (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM sync_attempts")).ShouldBe(0);
    }

    [Fact]
    public async Task Dos_workers_que_toman_el_mismo_job_a_la_vez_no_pisan_su_estado()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var seed = PostgresFixture.CreateContext(connectionString))
        {
            seed.SyncJobs.Add(NewJob("5501"));
            await seed.SaveChangesAsync();
        }

        await using var first = PostgresFixture.CreateContext(connectionString);
        await using var second = PostgresFixture.CreateContext(connectionString);
        var jobSeenByFirst = await first.SyncJobs.SingleAsync();
        var jobSeenBySecond = await second.SyncJobs.SingleAsync();

        jobSeenByFirst.Claim("worker-1", Now, Lease).IsSuccess.ShouldBeTrue();
        await first.SaveChangesAsync();
        jobSeenBySecond.Claim("worker-2", Now, Lease).IsSuccess.ShouldBeTrue();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        (await ScalarAsync<string>(connectionString, "SELECT locked_by FROM sync_jobs")).ShouldBe("worker-1");
    }

    [Fact]
    public async Task Los_mapeos_de_productos_y_clientes_se_guardan_y_son_unicos()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.ProductMappings.Add(ProductMapping.Create("TAL-650", "A00001", Now).Value);
            context.CustomerMappings.Add(CustomerMapping.Create(CustomerKey.ForRegistered(4521), "C12345678-5", Now).Value);
            await context.SaveChangesAsync();
        }

        await using (var reading = PostgresFixture.CreateContext(connectionString))
        {
            (await reading.ProductMappings.SingleAsync()).ItemCode.ShouldBe("A00001");
            (await reading.CustomerMappings.SingleAsync()).CardCode.ShouldBe("C12345678-5");
        }

        await using var duplicates = PostgresFixture.CreateContext(connectionString);
        duplicates.ProductMappings.Add(ProductMapping.Create("TAL-650", "A00002", Now).Value);
        var productError = await Should.ThrowAsync<DbUpdateException>(() => duplicates.SaveChangesAsync());
        productError.InnerException.ShouldBeOfType<PostgresException>().ConstraintName.ShouldBe("ux_product_mappings_sku");

        await using var duplicateCustomers = PostgresFixture.CreateContext(connectionString);
        duplicateCustomers.CustomerMappings.Add(CustomerMapping.Create(CustomerKey.ForRegistered(4521), "C99999999-9", Now).Value);
        var customerError = await Should.ThrowAsync<DbUpdateException>(() => duplicateCustomers.SaveChangesAsync());
        customerError.InnerException.ShouldBeOfType<PostgresException>().ConstraintName.ShouldBe("ux_customer_mappings_external_key");
    }

    private static SyncJob NewJob(string externalId, string payload = "{}", decimal? storeTotal = null) =>
        SyncJob.Create(SyncSources.WooCommerce, externalId, SyncJobTypes.Order, payload, storeTotal, Now).Value;

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
