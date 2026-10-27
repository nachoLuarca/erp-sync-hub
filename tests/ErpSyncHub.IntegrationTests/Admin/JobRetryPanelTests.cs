using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>El reintento manual desde el panel, de uno o varios jobs.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class JobRetryPanelTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly ResultError Transient = ResultError.Transient("sap.unreachable", "No se pudo conectar con SAP.");

    private static string Order => Fixtures.Read("woocommerce/order-processing.json");

    private static SyncJob NewJob(string externalId, string? payload = null, DateTimeOffset? createdAt = null) =>
        SyncJob.Create(SyncSources.WooCommerce, externalId, SyncJobTypes.Order, payload ?? Order, 100013.55m, createdAt ?? T0).Value;

    private static async Task SaveAsync(string connectionString, params object[] entities)
    {
        await using var context = PostgresFixture.CreateContext(connectionString);
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    /// <summary>Un job que el worker dio por perdido tras agotar sus intentos transitorios.</summary>
    private static (SyncJob Job, SyncAttempt Attempt) DeadLetter(string externalId = "5501", string? payload = null)
    {
        var job = NewJob(externalId, payload);
        var attempt = job.Claim("worker-1", T0, Lease).Value;
        job.MarkDeadLetter("worker-1", Transient, T0.AddSeconds(1));
        attempt.Fail(Transient, T0.AddSeconds(1));
        return (job, attempt);
    }

    private static (SyncJob Job, SyncAttempt Attempt) Failed(string externalId = "5502", string? payload = null)
    {
        var job = NewJob(externalId, payload);
        var attempt = job.Claim("worker-1", T0, Lease).Value;
        var error = ResultError.Rejected("order.unknown_sku", "El SKU 'X' no tiene mapeo ni existe en SAP.");
        job.MarkFailed("worker-1", error, T0.AddSeconds(1));
        attempt.Fail(error, T0.AddSeconds(1));
        return (job, attempt);
    }

    private static (SyncJob Job, SyncAttempt Attempt) Succeeded(string externalId = "5503")
    {
        var job = NewJob(externalId);
        var attempt = job.Claim("worker-1", T0, Lease).Value;
        job.MarkSucceeded("worker-1", new SapOrderReference(7, 1007, "C18765432-7", 24990m), T0.AddSeconds(1));
        attempt.Succeed(T0.AddSeconds(1));
        return (job, attempt);
    }

    private static (SyncJob Job, SyncAttempt Attempt) InProgress(string externalId = "5504")
    {
        var job = NewJob(externalId);
        var attempt = job.Claim("worker-1", DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).Value;
        return (job, attempt);
    }

    private Task<WebhookHost> StartAsync(
        Func<string, Task> seed,
        SapMockHost? sap = null,
        bool workerEnabled = false,
        Action<IWebHostBuilder>? configure = null) =>
        WebhookHost.StartAsync(postgres, configure, sap: sap, workerEnabled: workerEnabled, seed: seed);

    private static async Task<HttpResponseMessage> RetryAsync(WebhookHost host, Guid id) =>
        await host.Client.PostAsync($"/admin/jobs/{id}/retry", content: null);

    private static async Task<HttpResponseMessage> RetryManyAsync(WebhookHost host, params Guid[] ids) =>
        await host.Client.PostAsJsonAsync("/admin/jobs/retry", new { ids });

    [Fact]
    public async Task Reintentar_un_job_en_el_dead_letter_lo_deja_pendiente_con_un_presupuesto_nuevo_y_conserva_el_historial()
    {
        var (job, attempt) = DeadLetter();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));

        var response = await RetryAsync(host, job.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("pending");
        body.RootElement.GetProperty("cycleAttempts").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("attemptCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.RootElement.GetProperty("nextAttemptAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.RootElement.GetProperty("lastError").GetProperty("code").GetString().ShouldBe("sap.unreachable");
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_attempts")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT outcome FROM sync_attempts")).ShouldBe("failed");
    }

    [Fact]
    public async Task Reintentar_un_job_fallido_de_forma_definitiva_tambien_funciona()
    {
        var (job, attempt) = Failed();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));

        (await RetryAsync(host, job.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("pending");
    }

    [Fact]
    public async Task Reintentar_un_job_que_espera_su_reintento_se_salta_la_espera()
    {
        var job = NewJob("5501");
        var attempt = job.Claim("worker-1", T0, Lease).Value;
        job.ScheduleRetry("worker-1", Transient, DateTimeOffset.UtcNow.AddHours(1), T0.AddSeconds(1));
        attempt.Fail(Transient, T0.AddSeconds(1));
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));

        (await RetryAsync(host, job.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("pending");
        (await host.ScalarAsync<DateTimeOffset?>("SELECT next_attempt_at FROM sync_jobs")).ShouldBeNull();
    }

    [Fact]
    public async Task Un_job_reintentado_se_procesa_y_termina_en_SAP()
    {
        var (job, attempt) = DeadLetter();
        using var sap = new SapMockHost();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt), sap, workerEnabled: true);
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("dead_letter");

        (await RetryAsync(host, job.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job reintentado termine");

        (await host.ScalarAsync<int>("SELECT sap_doc_entry FROM sync_jobs")).ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT last_error_code FROM sync_jobs")).ShouldBeNull();
        (await host.ScalarAsync<string>("SELECT string_agg(outcome, ',' ORDER BY number) FROM sync_attempts")).ShouldBe("failed,succeeded");
        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(2);
    }

    [Fact]
    public async Task Reintentar_un_job_cuya_orden_ya_existe_en_SAP_no_la_duplica()
    {
        var (job, attempt) = DeadLetter();
        using var sap = new SapMockHost();
        await sap.PostAsync(
            "Orders",
            new { CardCode = "C00001", NumAtCard = "WC-5501", DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1, UnitPrice = 100 } } });
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt), sap, workerEnabled: true);

        await RetryAsync(host, job.Id);
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job reintentado termine");

        using var orders = await sap.GetAsync("Orders");
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Un_reintento_manual_concede_un_presupuesto_de_intentos_nuevo()
    {
        var (job, attempt) = DeadLetter();
        using var sap = new SapMockHost();
        await sap.SetFaultsAsync(new { Outage = true });
        await using var host = await StartAsync(
            cs => SaveAsync(cs, job, attempt),
            sap,
            workerEnabled: true,
            configure: builder =>
            {
                builder.UseSetting("Worker:Retry:MaxAttempts", "3");
                builder.UseSetting("Worker:Retry:BaseDelay", "00:00:00.050");
                builder.UseSetting("Worker:Retry:MaxDelay", "00:00:00.100");
            });

        await RetryAsync(host, job.Id);
        await host.WaitUntilAsync("SELECT bool_and(status = 'dead_letter' AND attempt_count = 4) FROM sync_jobs", "que agote los 3 intentos nuevos");

        // 1 intento previo + 3 nuevos: el presupuesto se reinició en vez de acumularse.
        (await host.ScalarAsync<int>("SELECT attempt_count FROM sync_jobs")).ShouldBe(4);
        (await host.ScalarAsync<int>("SELECT cycle_attempts FROM sync_jobs")).ShouldBe(3);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("processing")]
    public async Task No_se_puede_reintentar_un_job_en_proceso_ni_uno_que_ya_termino_con_exito(string state)
    {
        var (job, attempt) = state == "succeeded" ? Succeeded() : InProgress();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));

        var response = await RetryAsync(host, job.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("code").GetString().ShouldBe("job.not_retryable");
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe(state);
    }

    [Fact]
    public async Task Reintentar_un_job_inexistente_responde_404()
    {
        await using var host = await StartAsync(_ => Task.CompletedTask);

        var response = await RetryAsync(host, Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Reintentar_varios_jobs_responde_un_resultado_por_id_aunque_alguno_no_se_pueda()
    {
        var dead = DeadLetter("5501");
        var failed = Failed("5502");
        var succeeded = Succeeded("5503");
        var missing = Guid.NewGuid();
        await using var host = await StartAsync(cs => SaveAsync(cs, dead.Job, dead.Attempt, failed.Job, failed.Attempt, succeeded.Job, succeeded.Attempt));

        var response = await RetryManyAsync(host, dead.Job.Id, failed.Job.Id, succeeded.Job.Id, missing);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("requested").GetInt32().ShouldBe(4);
        body.RootElement.GetProperty("retried").GetInt32().ShouldBe(2);
        var results = body.RootElement.GetProperty("results").EnumerateArray().ToDictionary(
            result => result.GetProperty("id").GetGuid(),
            result => (Outcome: result.GetProperty("outcome").GetString(), Status: result.GetProperty("status").GetString()));
        results[dead.Job.Id].ShouldBe(("retried", "pending"));
        results[failed.Job.Id].ShouldBe(("retried", "pending"));
        results[succeeded.Job.Id].ShouldBe(("not_retryable", "succeeded"));
        results[missing].ShouldBe(("not_found", null));
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs WHERE status = 'pending'")).ShouldBe(2);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs WHERE status = 'succeeded'")).ShouldBe(1);
    }

    [Fact]
    public async Task Los_ids_repetidos_se_reintentan_una_sola_vez()
    {
        var (job, attempt) = DeadLetter();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));

        var response = await RetryManyAsync(host, job.Id, job.Id, job.Id);

        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("requested").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("retried").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Reintentar_hasta_100_jobs_a_la_vez_funciona()
    {
        var entities = new List<object>();
        var ids = new List<Guid>();
        for (var n = 0; n < 100; n++)
        {
            var (job, attempt) = DeadLetter($"{9000 + n}");
            entities.AddRange([job, attempt]);
            ids.Add(job.Id);
        }

        await using var host = await StartAsync(cs => SaveAsync(cs, [.. entities]));

        var response = await RetryManyAsync(host, [.. ids]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("retried").GetInt32().ShouldBe(100);
        (await host.ScalarAsync<long>("SELECT count(*) FROM sync_jobs WHERE status = 'pending'")).ShouldBe(100);
    }

    [Fact]
    public async Task Mas_de_100_jobs_en_una_solicitud_se_rechazan_sin_reintentar_ninguno()
    {
        var (job, attempt) = DeadLetter();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));
        var ids = new[] { job.Id }.Concat(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())).ToArray();

        var response = await RetryManyAsync(host, ids);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("100");
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("dead_letter");
    }

    [Theory]
    [InlineData("""{"ids":[]}""")]
    [InlineData("""{"ids":null}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    public async Task Una_solicitud_sin_ids_responde_400(string json)
    {
        await using var host = await StartAsync(_ => Task.CompletedTask);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync("/admin/jobs/retry", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Un_id_que_no_es_un_guid_responde_400()
    {
        await using var host = await StartAsync(_ => Task.CompletedTask);
        using var content = new StringContent("""{"ids":["no-es-un-guid"]}""", System.Text.Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync("/admin/jobs/retry", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var detail = await response.Content.ReadAsStringAsync();
        detail.ShouldContain("formato válido");
        detail.ShouldNotContain("RetryJobsRequest"); // el texto interno de la excepción no se expone
    }

    [Fact]
    public async Task Un_json_roto_responde_400_y_no_500()
    {
        await using var host = await StartAsync(_ => Task.CompletedTask);
        using var content = new StringContent("{\"ids\": [", System.Text.Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync("/admin/jobs/retry", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sin_la_clave_del_panel_no_se_puede_reintentar_nada()
    {
        var (job, attempt) = DeadLetter();
        await using var host = await StartAsync(cs => SaveAsync(cs, job, attempt));
        using var anonymous = host.CreateAnonymousClient();

        var one = await anonymous.PostAsync($"/admin/jobs/{job.Id}/retry", content: null);
        var many = await anonymous.PostAsJsonAsync("/admin/jobs/retry", new { ids = new[] { job.Id } });

        one.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        many.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.ScalarAsync<string>("SELECT status FROM sync_jobs")).ShouldBe("dead_letter");
    }

    [Fact]
    public async Task El_reintento_manual_queda_en_el_log_sin_datos_personales()
    {
        var (job, attempt) = DeadLetter();
        var sink = new CollectingSink();
        await using var host = await StartAsync(
            cs => SaveAsync(cs, job, attempt),
            configure: builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        await RetryAsync(host, job.Id);

        var entry = sink.Events.Single(logEvent => logEvent.RenderMessage().Contains("Reintento manual", StringComparison.Ordinal));
        entry.RenderMessage().ShouldContain("1 de 1");
        sink.Events.Select(logEvent => logEvent.RenderMessage()).ShouldNotContain(message => message.Contains("Valentina", StringComparison.Ordinal));
    }
}
