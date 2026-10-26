using System.Net;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Inventory;

/// <summary>Varias instancias del hub sobre la misma base de datos: nunca corren dos sincronizaciones a la vez.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class InventorySyncMultiInstanceTests(PostgresFixture postgres) : IDisposable
{
    private readonly FakeWooCommerceHandler _store = new() { Delay = TimeSpan.FromMilliseconds(700) };
    private readonly SapMockHost _sap = new();

    public void Dispose()
    {
        _sap.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task Dos_instancias_con_sincronizacion_periodica_nunca_se_solapan()
    {
        _store.AddProduct(11, "A00001");
        var configure = InventorySyncHost.Configure(_store, interval: TimeSpan.FromSeconds(1));
        await using var first = await WebhookHost.StartAsync(postgres, configure, sap: _sap);
        await using var second = WebhookHost.StartOnDatabase(first.ConnectionString, _sap, workerEnabled: false, configure);

        var maxRunning = 0L;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            maxRunning = Math.Max(maxRunning, await first.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs WHERE status = 'running'"));
            await Task.Delay(40);
        }

        maxRunning.ShouldBeLessThanOrEqualTo(1);
        (await first.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs WHERE status = 'succeeded'")).ShouldBeGreaterThanOrEqualTo(2);
        (await first.ScalarAsync<long>(
            """
            SELECT count(*) FROM inventory_sync_runs a
            JOIN inventory_sync_runs b ON a.id < b.id AND a.started_at < b.finished_at AND b.started_at < a.finished_at
            """)).ShouldBe(0);
    }

    [Fact]
    public async Task Un_disparo_manual_en_una_instancia_mientras_otra_sincroniza_responde_409()
    {
        _store.AddProduct(11, "A00001");
        var configure = InventorySyncHost.Configure(_store, settings: new Dictionary<string, string> { ["Sync:Inventory:Enabled"] = "false" });
        await using var first = await WebhookHost.StartAsync(postgres, configure, sap: _sap);
        await using var second = WebhookHost.StartOnDatabase(first.ConnectionString, _sap, workerEnabled: false, configure);

        var running = first.Client.PostAsync("/admin/sync/inventory", content: null);
        await first.WaitUntilAsync("SELECT count(*) = 1 FROM inventory_sync_runs WHERE status = 'running'", "que la primera sincronización esté en curso");
        var rejected = await second.Client.PostAsync("/admin/sync/inventory", content: null);

        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await running).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(1);
    }

    [Fact]
    public async Task Si_una_instancia_cae_en_plena_sincronizacion_la_siguiente_cierra_la_huerfana_y_sigue()
    {
        _store.AddProduct(11, "A00001");
        var configure = InventorySyncHost.Configure(_store, settings: new Dictionary<string, string> { ["Sync:Inventory:Enabled"] = "false" });
        await using var host = await WebhookHost.StartAsync(
            postgres,
            configure,
            sap: _sap,
            seed: async connectionString =>
            {
                // Una ejecución que quedó "en curso" porque la instancia que la llevaba murió.
                await using var connection = new Npgsql.NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new Npgsql.NpgsqlCommand(
                    """
                    INSERT INTO inventory_sync_runs (id, trigger, status, instance, "full", started_at, items_read, items_updated, items_unchanged, items_skipped, items_failed)
                    VALUES (gen_random_uuid(), 'scheduled', 'running', 'instancia-muerta', false, now() - interval '2 hours', 0, 0, 0, 0, 0)
                    """,
                    connection);
                await command.ExecuteNonQueryAsync();
            });

        var response = await host.Client.PostAsync("/admin/sync/inventory", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ScalarAsync<string>("SELECT status FROM inventory_sync_runs WHERE instance = 'instancia-muerta'")).ShouldBe("abandoned");
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs WHERE status = 'succeeded'")).ShouldBe(1);
    }
}
