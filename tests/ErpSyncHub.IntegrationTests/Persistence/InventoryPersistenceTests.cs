using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class InventoryPersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task La_foto_se_guarda_y_se_lee_con_todos_sus_datos()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650", "A00001", 45, 39990.5m, Now).Value);
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);
        var stored = await reading.ItemSnapshots.SingleAsync();

        stored.Sku.ShouldBe("TAL-650");
        stored.ItemCode.ShouldBe("A00001");
        stored.StockQuantity.ShouldBe(45);
        stored.Price.ShouldBe(39990.5m);
        stored.SyncedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task La_foto_sin_precio_guarda_null()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650", "A00001", 0, null, Now).Value);
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);

        (await reading.ItemSnapshots.SingleAsync()).Price.ShouldBeNull();
    }

    [Fact]
    public async Task No_puede_haber_dos_fotos_del_mismo_SKU()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);
        context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650", "A00001", 1, null, Now).Value);
        context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650", "A00002", 2, null, Now).Value);

        var failure = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());

        failure.InnerException.ShouldBeOfType<PostgresException>().ConstraintName.ShouldBe("ux_item_snapshots_sku");
    }

    [Fact]
    public async Task Varios_SKU_pueden_apuntar_al_mismo_articulo()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);
        context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650", "A00001", 1, null, Now).Value);
        context.ItemSnapshots.Add(ItemSnapshot.Create("TAL-650-AZUL", "A00001", 1, null, Now).Value);

        (await context.SaveChangesAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task La_base_rechaza_stock_negativo()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO item_snapshots (id, sku, item_code, stock_quantity, synced_at) VALUES (gen_random_uuid(), 'X', 'Y', -1, now())",
            connection);

        var failure = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        failure.ConstraintName.ShouldBe("ck_item_snapshots_stock_quantity");
    }

    [Fact]
    public async Task La_ejecucion_se_guarda_con_sus_contadores_y_su_estado_en_snake_case()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var run = InventorySyncRun.Start(InventorySyncTrigger.Manual, "hub-1", full: false, new DateOnly(2026, 3, 9), Now).Value;
        run.Complete(new InventorySyncCounts(Read: 10, Updated: 4, Unchanged: 3, Skipped: 2, Failed: 1), Now.AddMinutes(1));
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.InventorySyncRuns.Add(run);
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);
        var stored = await reading.InventorySyncRuns.SingleAsync();

        stored.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        stored.Trigger.ShouldBe(InventorySyncTrigger.Manual);
        stored.ChangedSince.ShouldBe(new DateOnly(2026, 3, 9));
        stored.StartedAt.ShouldBe(Now);
        stored.FinishedAt.ShouldBe(Now.AddMinutes(1));
        (stored.ItemsRead, stored.ItemsUpdated, stored.ItemsUnchanged, stored.ItemsSkipped, stored.ItemsFailed).ShouldBe((10, 4, 3, 2, 1));

        var raw = await ScalarAsync(connectionString, "SELECT status || '/' || trigger FROM inventory_sync_runs");
        raw.ShouldBe("completed_with_errors/manual");
    }

    [Fact]
    public async Task La_ejecucion_fallida_guarda_el_error()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var run = InventorySyncRun.Start(InventorySyncTrigger.Scheduled, "hub-1", full: true, null, Now).Value;
        run.Fail(ResultError.Transient("sap.unreachable", "No se pudo conectar con SAP."), default, Now.AddSeconds(5));
        await using (var context = PostgresFixture.CreateContext(connectionString))
        {
            context.InventorySyncRuns.Add(run);
            await context.SaveChangesAsync();
        }

        await using var reading = PostgresFixture.CreateContext(connectionString);
        var stored = await reading.InventorySyncRuns.SingleAsync();

        stored.Status.ShouldBe(InventorySyncStatus.Failed);
        stored.ErrorCode.ShouldBe("sap.unreachable");
        stored.ChangedSince.ShouldBeNull();
        stored.Full.ShouldBeTrue();
    }

    [Fact]
    public async Task La_base_rechaza_un_estado_desconocido()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO inventory_sync_runs (id, trigger, status, instance, \"full\", started_at, items_read, items_updated, items_unchanged, items_skipped, items_failed) " +
            "VALUES (gen_random_uuid(), 'manual', 'inventado', 'x', false, now(), 0, 0, 0, 0, 0)",
            connection);

        var failure = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        failure.MessageText.ShouldContain("ck_inventory_sync_runs_status");
    }

    [Fact]
    public async Task El_indice_de_ejecuciones_recientes_es_descendente()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var definition = await ScalarAsync(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_inventory_sync_runs_started_at'");

        definition.ShouldContain("started_at DESC");
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
