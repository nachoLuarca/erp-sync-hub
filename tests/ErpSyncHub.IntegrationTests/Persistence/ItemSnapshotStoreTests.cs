using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class ItemSnapshotStoreTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Confirmar_crea_la_foto_y_se_puede_leer_por_SKU()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);

        await host.UseAsync<IItemSnapshotStore>(store =>
            store.ConfirmAsync([new SnapshotConfirmation("TAL-650", "A00001", 40, 39990m)], CancellationToken.None));
        var found = await host.UseAsync<IItemSnapshotStore, IReadOnlyDictionary<string, Domain.Inventory.ItemSnapshot>>(
            store => store.FindAsync(["TAL-650", "OTRO"], CancellationToken.None));

        var snapshot = found.ShouldHaveSingleItem().Value;
        snapshot.Sku.ShouldBe("TAL-650");
        snapshot.ItemCode.ShouldBe("A00001");
        snapshot.StockQuantity.ShouldBe(40);
        snapshot.Price.ShouldBe(39990m);
        snapshot.SyncedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Confirmar_otra_vez_actualiza_en_vez_de_duplicar()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);
        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", 40, 39990m));

        host.Time.Advance(TimeSpan.FromMinutes(15));
        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", 35, 41990m));

        var snapshot = await Find(host, "TAL-650");
        snapshot.StockQuantity.ShouldBe(35);
        snapshot.Price.ShouldBe(41990m);
        snapshot.SyncedAt.ShouldBe(Now.AddMinutes(15));
        (await Count(host)).ShouldBe(1);
    }

    [Fact]
    public async Task Lo_que_no_se_envio_conserva_su_valor_anterior()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);
        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", 40, 39990m));

        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", 12, null));
        var afterStock = await Find(host, "TAL-650");
        afterStock.StockQuantity.ShouldBe(12);
        afterStock.Price.ShouldBe(39990m);

        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", null, 45000m));
        var afterPrice = await Find(host, "TAL-650");
        afterPrice.StockQuantity.ShouldBe(12);
        afterPrice.Price.ShouldBe(45000m);
    }

    [Fact]
    public async Task Confirmar_solo_el_precio_de_un_SKU_nuevo_deja_el_stock_en_cero()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);

        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", null, 100m));

        var snapshot = await Find(host, "TAL-650");
        snapshot.StockQuantity.ShouldBe(0);
        snapshot.Price.ShouldBe(100m);
    }

    [Fact]
    public async Task Si_el_SKU_pasa_a_otro_articulo_la_foto_lo_sigue()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);
        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00001", 1, null));

        await Confirm(host, new SnapshotConfirmation("TAL-650", "A00002", 1, null));

        (await Find(host, "TAL-650")).ItemCode.ShouldBe("A00002");
    }

    [Fact]
    public async Task Confirma_varios_productos_a_la_vez()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);

        await Confirm(host, new SnapshotConfirmation("A", "A00001", 1, 10m), new SnapshotConfirmation("B", "A00002", 2, 20m));

        (await Count(host)).ShouldBe(2);
    }

    [Fact]
    public async Task Buscar_sin_SKU_no_consulta_nada()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);

        var found = await host.UseAsync<IItemSnapshotStore, IReadOnlyDictionary<string, Domain.Inventory.ItemSnapshot>>(
            store => store.FindAsync([], CancellationToken.None));

        found.ShouldBeEmpty();
    }

    [Fact]
    public async Task El_almacen_de_mapeos_devuelve_todos_los_SKU_de_cada_articulo()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres, Now);
        await host.UseAsync<IMappingStore>(async mappings =>
        {
            await mappings.SaveProductMappingAsync("TAL-650-AZUL", "A00001", CancellationToken.None);
            await mappings.SaveProductMappingAsync("TAL-650", "A00001", CancellationToken.None);
            await mappings.SaveProductMappingAsync("MART-16", "A00007", CancellationToken.None);
        });

        var found = await host.UseAsync<IMappingStore, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            mappings => mappings.FindSkusAsync(["A00001", "A00099"], CancellationToken.None));
        var none = await host.UseAsync<IMappingStore, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            mappings => mappings.FindSkusAsync([], CancellationToken.None));

        found.Keys.ShouldBe(["A00001"]);
        found["A00001"].ShouldBe(["TAL-650", "TAL-650-AZUL"]);
        none.ShouldBeEmpty();
    }

    private static Task Confirm(InfrastructureHost host, params SnapshotConfirmation[] confirmations) =>
        host.UseAsync<IItemSnapshotStore>(store => store.ConfirmAsync(confirmations, CancellationToken.None));

    private static async Task<Domain.Inventory.ItemSnapshot> Find(InfrastructureHost host, string sku)
    {
        var found = await host.UseAsync<IItemSnapshotStore, IReadOnlyDictionary<string, Domain.Inventory.ItemSnapshot>>(
            store => store.FindAsync([sku], CancellationToken.None));
        return found[sku];
    }

    private static async Task<long> Count(InfrastructureHost host)
    {
        await using var connection = new Npgsql.NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM item_snapshots", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
