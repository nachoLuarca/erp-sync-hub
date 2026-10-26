using System.Net;
using System.Text.Json;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>Las métricas básicas del panel: jobs por estado, atraso de la cola y última sincronización de inventario.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class MetricsPanelTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly ResultError Transient = ResultError.Transient("sap.unreachable", "No se pudo conectar con SAP.");

    private static SyncJob NewJob(string externalId, DateTimeOffset createdAt) =>
        SyncJob.Create(SyncSources.WooCommerce, externalId, SyncJobTypes.Order, "{}", 100m, createdAt).Value;

    private static async Task SeedAsync(string connectionString)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = PostgresFixture.CreateContext(connectionString);

        foreach (var id in new[] { "1", "2" })
        {
            var job = NewJob(id, now.AddHours(-5));
            var attempt = job.Claim("worker-1", now.AddHours(-5), Lease).Value;
            job.MarkSucceeded("worker-1", new SapOrderReference(1, 1001, "C1", 100m), now.AddHours(-5));
            attempt.Succeed(now.AddHours(-5));
            context.AddRange(job, attempt);
        }

        var failed = NewJob("3", now.AddHours(-4));
        var failedAttempt = failed.Claim("worker-1", now.AddHours(-4), Lease).Value;
        failed.MarkFailed("worker-1", ResultError.Rejected("order.unknown_sku", "SKU sin mapeo."), now.AddHours(-4));
        failedAttempt.Fail(ResultError.Rejected("order.unknown_sku", "SKU sin mapeo."), now.AddHours(-4));
        context.AddRange(failed, failedAttempt);

        var retrying = NewJob("4", now.AddHours(-3));
        var retryAttempt = retrying.Claim("worker-1", now.AddHours(-3), Lease).Value;
        retrying.ScheduleRetry("worker-1", Transient, now.AddMinutes(10), now.AddHours(-3));
        retryAttempt.Fail(Transient, now.AddHours(-3));
        var retryingLater = NewJob("5", now.AddHours(-3));
        var laterAttempt = retryingLater.Claim("worker-1", now.AddHours(-3), Lease).Value;
        retryingLater.ScheduleRetry("worker-1", Transient, now.AddMinutes(45), now.AddHours(-3));
        laterAttempt.Fail(Transient, now.AddHours(-3));
        context.AddRange(retrying, retryAttempt, retryingLater, laterAttempt);

        var dead = NewJob("6", now.AddHours(-2));
        var deadAttempt = dead.Claim("worker-1", now.AddHours(-2), Lease).Value;
        dead.MarkDeadLetter("worker-1", Transient, now.AddHours(-2));
        deadAttempt.Fail(Transient, now.AddHours(-2));
        context.AddRange(dead, deadAttempt);

        context.Add(NewJob("7", now.AddMinutes(-30)));
        context.Add(NewJob("8", now.AddMinutes(-10)));

        var oldRun = InventorySyncRun.Start(InventorySyncTrigger.Scheduled, "hub-1", full: false, null, now.AddHours(-2)).Value;
        oldRun.Complete(new InventorySyncCounts(24, 3, 0, 21, 0), now.AddHours(-2).AddSeconds(2));
        var newRun = InventorySyncRun.Start(InventorySyncTrigger.Manual, "hub-1", full: false, null, now.AddMinutes(-5)).Value;
        newRun.Fail(ResultError.Transient("store.unreachable", "No se pudo conectar con la tienda."), new InventorySyncCounts(24, 0, 0, 0, 24), now.AddMinutes(-5).AddSeconds(1));
        context.AddRange(oldRun, newRun);

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Cuenta_los_jobs_por_estado_e_incluye_los_estados_en_cero()
    {
        await using var host = await WebhookHost.StartAsync(postgres, seed: SeedAsync);

        var response = await host.Client.GetAsync("/admin/metrics");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        var jobs = body.RootElement.GetProperty("jobs");
        jobs.GetProperty("total").GetInt32().ShouldBe(8);
        var byStatus = jobs.GetProperty("byStatus");
        byStatus.GetProperty("pending").GetInt32().ShouldBe(2);
        byStatus.GetProperty("processing").GetInt32().ShouldBe(0);
        byStatus.GetProperty("retrying").GetInt32().ShouldBe(2);
        byStatus.GetProperty("succeeded").GetInt32().ShouldBe(2);
        byStatus.GetProperty("failed").GetInt32().ShouldBe(1);
        byStatus.GetProperty("dead_letter").GetInt32().ShouldBe(1);
        byStatus.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["pending", "processing", "retrying", "succeeded", "failed", "dead_letter"], ignoreOrder: true);
    }

    [Fact]
    public async Task Informa_la_antiguedad_del_pendiente_mas_viejo_y_el_proximo_reintento()
    {
        await using var host = await WebhookHost.StartAsync(postgres, seed: SeedAsync);

        using var body = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/metrics"));

        var jobs = body.RootElement.GetProperty("jobs");
        var oldest = jobs.GetProperty("oldestPending");
        oldest.GetProperty("ageSeconds").GetDouble().ShouldBeInRange(29 * 60.0, 33 * 60.0); // creado hace unos 30 minutos
        var generatedAt = body.RootElement.GetProperty("generatedAt").GetDateTimeOffset();
        (generatedAt - oldest.GetProperty("createdAt").GetDateTimeOffset()).TotalMinutes.ShouldBeInRange(29, 33);
        var nextRetry = jobs.GetProperty("nextRetryAt").GetDateTimeOffset();
        (nextRetry - generatedAt).TotalMinutes.ShouldBeInRange(8, 11); // el más próximo de los dos que esperan
    }

    [Fact]
    public async Task Trae_la_ultima_ejecucion_de_inventario_aunque_haya_fallado_y_la_ultima_exitosa()
    {
        await using var host = await WebhookHost.StartAsync(postgres, seed: SeedAsync);

        using var body = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/metrics"));

        var inventory = body.RootElement.GetProperty("inventorySync");
        var last = inventory.GetProperty("lastRun");
        last.GetProperty("status").GetString().ShouldBe("failed");
        last.GetProperty("trigger").GetString().ShouldBe("manual");
        last.GetProperty("error").GetProperty("code").GetString().ShouldBe("store.unreachable");
        var lastOk = inventory.GetProperty("lastSucceededRun");
        lastOk.GetProperty("status").GetString().ShouldBe("succeeded");
        lastOk.GetProperty("items").GetProperty("updated").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Sin_datos_todo_es_cero_o_nulo()
    {
        await using var host = await WebhookHost.StartAsync(postgres);

        using var body = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/metrics"));

        var jobs = body.RootElement.GetProperty("jobs");
        jobs.GetProperty("total").GetInt32().ShouldBe(0);
        jobs.GetProperty("byStatus").EnumerateObject().ShouldAllBe(property => property.Value.GetInt32() == 0);
        jobs.GetProperty("oldestPending").ValueKind.ShouldBe(JsonValueKind.Null);
        jobs.GetProperty("nextRetryAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var inventory = body.RootElement.GetProperty("inventorySync");
        inventory.GetProperty("lastRun").ValueKind.ShouldBe(JsonValueKind.Null);
        inventory.GetProperty("lastSucceededRun").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Los_totales_coinciden_con_el_listado_de_jobs()
    {
        await using var host = await WebhookHost.StartAsync(postgres, seed: SeedAsync);
        using var metrics = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/metrics"));

        foreach (var status in metrics.RootElement.GetProperty("jobs").GetProperty("byStatus").EnumerateObject())
        {
            using var page = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync($"/admin/jobs?status={status.Name}"));
            page.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(status.Value.GetInt32(), status.Name);
        }
    }

    [Fact]
    public async Task Reintentar_un_job_se_refleja_en_las_metricas()
    {
        await using var host = await WebhookHost.StartAsync(postgres, seed: SeedAsync);
        using var dead = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/jobs?status=dead_letter"));
        var id = dead.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        await host.Client.PostAsync($"/admin/jobs/{id}/retry", content: null);

        using var body = await WebhookHost.ReadJsonAsync(await host.Client.GetAsync("/admin/metrics"));
        var byStatus = body.RootElement.GetProperty("jobs").GetProperty("byStatus");
        byStatus.GetProperty("dead_letter").GetInt32().ShouldBe(0);
        byStatus.GetProperty("pending").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Sin_la_clave_del_panel_las_metricas_responden_401()
    {
        await using var host = await WebhookHost.StartAsync(postgres);
        using var anonymous = host.CreateAnonymousClient();

        (await anonymous.GetAsync("/admin/metrics")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
