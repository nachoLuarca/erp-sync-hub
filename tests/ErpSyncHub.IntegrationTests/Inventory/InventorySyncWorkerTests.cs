using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Inventory;

/// <summary>El hub completo sincronizando stock y precios: SAP simulado, tienda simulada y PostgreSQL real.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class InventorySyncWorkerTests(PostgresFixture postgres) : IDisposable
{
    private static string SucceededRuns(int atLeast) =>
        $"SELECT count(*) >= {atLeast} FROM inventory_sync_runs WHERE status = 'succeeded'";

    private readonly FakeWooCommerceHandler _store = new();
    private readonly SapMockHost _sap = new();

    public void Dispose()
    {
        _sap.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task Sincroniza_al_iniciar_y_registra_la_ejecucion_con_sus_contadores()
    {
        var drill = _store.AddProduct(11, "A00001");
        var tape = _store.AddProduct(12, "A00010");
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store),
            sap: _sap);

        await host.WaitUntilAsync(SucceededRuns(1), "que termine la primera sincronización");

        drill.StockQuantity.ShouldBe(45);
        drill.RegularPrice.ShouldBe("39990");
        tape.StockQuantity.ShouldBe(300);
        tape.RegularPrice.ShouldBe("2990");
        (await host.ScalarAsync<int>("SELECT items_read FROM inventory_sync_runs")).ShouldBe(24);
        (await host.ScalarAsync<int>("SELECT items_updated FROM inventory_sync_runs")).ShouldBe(2);
        (await host.ScalarAsync<int>("SELECT items_skipped FROM inventory_sync_runs")).ShouldBe(22); // sin producto en la tienda
        (await host.ScalarAsync<int>("SELECT items_failed FROM inventory_sync_runs")).ShouldBe(0);
        (await host.ScalarAsync<string>("SELECT trigger FROM inventory_sync_runs")).ShouldBe("scheduled");
        (await host.ScalarAsync<long>("SELECT count(*) FROM item_snapshots")).ShouldBe(2);
        (await host.ScalarAsync<long>("SELECT count(*) FROM product_mappings")).ShouldBe(2);
    }

    [Fact]
    public async Task La_siguiente_ejecucion_solo_envia_lo_que_cambio_en_SAP()
    {
        var drill = _store.AddProduct(11, "A00001");
        var tape = _store.AddProduct(12, "A00010");
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store, interval: TimeSpan.FromSeconds(1)),
            sap: _sap);
        await host.WaitUntilAsync(SucceededRuns(1), "que termine la primera sincronización");

        await _sap.AdjustItemAsync("A00001", new { InStock = 10, Price = 41990 });
        await host.WaitUntilAsync(SucceededRuns(3), "que terminen más sincronizaciones");

        drill.StockQuantity.ShouldBe(10);
        drill.RegularPrice.ShouldBe("41990");
        tape.StockQuantity.ShouldBe(300);
        var changes = _store.Batches.Select(batch => batch["update"]!.AsArray()).ToList();
        changes.Count.ShouldBe(2); // la primera pasada (2 productos) y la del cambio (1 producto)
        changes[0].Count.ShouldBe(2);
        changes[1].Count.ShouldBe(1);
        changes[1][0]!["id"]!.GetValue<long>().ShouldBe(11);
    }

    [Fact]
    public async Task Los_logs_de_una_ejecucion_llevan_su_identificador_como_propiedad()
    {
        _store.AddProduct(11, "A00001");
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store, sink: sink),
            sap: _sap);
        await host.WaitUntilAsync(SucceededRuns(1), "que termine la sincronización");
        var runId = await host.ScalarAsync<Guid>("SELECT id FROM inventory_sync_runs");

        var runEvents = sink.Events.Where(logEvent => logEvent.Properties.ContainsKey("InventoryRunId")).ToList();

        runEvents.Count.ShouldBeGreaterThanOrEqualTo(3); // inicio, advertencia de productos sin tienda, fin
        runEvents.ShouldAllBe(logEvent => logEvent.Properties["InventoryRunId"].ToString().Trim('"') == runId.ToString());
        runEvents.ShouldAllBe(logEvent => logEvent.Properties["Trigger"].ToString().Trim('"') == "Scheduled");
        var messages = runEvents.Select(logEvent => logEvent.RenderMessage()).ToList();
        messages.ShouldContain(message => message.Contains("iniciada", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("23 artículos de SAP no tienen producto en la tienda", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("1 actualizados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sin_la_tienda_configurada_no_sincroniza_y_lo_dice_claramente_al_arrancar()
    {
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(store: null, interval: TimeSpan.FromSeconds(1), sink: sink),
            sap: _sap);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(0);
        var warning = sink.Events.Single(logEvent => logEvent.Level == LogEventLevel.Warning && logEvent.RenderMessage().Contains("DESACTIVADA", StringComparison.Ordinal));
        warning.RenderMessage().ShouldContain("Store__WooCommerce__BaseUrl");
        warning.RenderMessage().ShouldContain("Store__WooCommerce__ConsumerSecret");
    }

    [Fact]
    public async Task Con_Enabled_en_falso_no_sincroniza_y_lo_registra()
    {
        _store.AddProduct(11, "A00001");
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store, sink: sink, settings: new Dictionary<string, string> { ["Sync:Inventory:Enabled"] = "false" }),
            sap: _sap);

        await Task.Delay(TimeSpan.FromSeconds(1));

        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(0);
        _store.Requests.ShouldBeEmpty();
        sink.Events.ShouldContain(logEvent => logEvent.RenderMessage().Contains("desactivada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Al_arrancar_registra_la_configuracion_con_que_sincroniza()
    {
        _store.AddProduct(11, "A00001");
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(
                _store,
                sink: sink,
                settings: new Dictionary<string, string> { ["Sync:Inventory:Warehouses"] = "01,02", ["Sync:Inventory:PriceList"] = "2" }),
            sap: _sap);
        await host.WaitUntilAsync(SucceededRuns(1), "que termine la sincronización");

        var start = sink.Events.Single(logEvent => logEvent.RenderMessage().Contains("activa:", StringComparison.Ordinal));

        start.RenderMessage().ShouldContain("lista de precios 2");
        start.RenderMessage().ShouldContain("01,02");
    }

    [Fact]
    public async Task Los_almacenes_configurados_determinan_el_stock_publicado()
    {
        var drill = _store.AddProduct(11, "A00001");
        await _sap.AdjustItemAsync("A00001", new { WarehouseCode = "02", InStock = 5 });
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store, settings: new Dictionary<string, string> { ["Sync:Inventory:Warehouses"] = "01,02" }),
            sap: _sap);

        await host.WaitUntilAsync(SucceededRuns(1), "que termine la sincronización");

        drill.StockQuantity.ShouldBe(50); // 45 del almacén 01 más 5 del 02
    }

    [Fact]
    public async Task Si_la_tienda_rechaza_las_credenciales_la_ejecucion_queda_fallida_y_el_hub_sigue_vivo()
    {
        _store.AddProduct(11, "A00001");
        _store.RespondNextWith(System.Net.HttpStatusCode.Unauthorized, """{"code":"woocommerce_rest_cannot_view","message":"Sin permiso.","data":{"status":401}}""");
        await using var host = await WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(_store, interval: TimeSpan.FromSeconds(1)),
            sap: _sap);

        await host.WaitUntilAsync("SELECT count(*) >= 1 FROM inventory_sync_runs WHERE status = 'failed'", "que la primera ejecución falle");
        (await host.ScalarAsync<string>("SELECT error_code FROM inventory_sync_runs WHERE status = 'failed' LIMIT 1")).ShouldBe("store.unauthorized");

        // La siguiente ejecución, con la tienda ya respondiendo, termina bien: el servicio no murió.
        await host.WaitUntilAsync(SucceededRuns(1), "que una ejecución posterior termine bien");
        (await host.Client.GetAsync("/health")).IsSuccessStatusCode.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Sync:Inventory:Interval", "00:00:00", "Sync__Inventory__Interval")]
    [InlineData("Sync:Inventory:Interval", "2.00:00:00", "Sync__Inventory__Interval")]
    [InlineData("Sync:Inventory:Warehouses", " , ", "Sync__Inventory__Warehouses")]
    [InlineData("Sync:Inventory:Warehouses", "01,ALMACEN-DEMASIADO-LARGO", "Sync__Inventory__Warehouses")]
    [InlineData("Sync:Inventory:PriceList", "0", "Sync__Inventory__PriceList")]
    [InlineData("Sync:Inventory:PriceDecimals", "5", "Sync__Inventory__PriceDecimals")]
    [InlineData("Sync:Inventory:SapPageSize", "0", "Sync__Inventory__SapPageSize")]
    [InlineData("Sync:Inventory:TimeZoneId", "Marte/Olimpo", "TimeZoneId")]
    public async Task Una_configuracion_invalida_impide_arrancar_y_nombra_la_variable(string key, string value, string expected)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(
            connectionString,
            configure: builder => builder.UseSetting(key, value));

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task Credenciales_de_la_tienda_con_formato_invalido_impiden_arrancar_y_nombran_la_variable()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(
            connectionString,
            configure: builder =>
            {
                builder.UseSetting("Store:WooCommerce:BaseUrl", "https://tienda.example.com");
                builder.UseSetting("Store:WooCommerce:ConsumerKey", "ck_corta");
                builder.UseSetting("Store:WooCommerce:ConsumerSecret", FakeWooCommerceHandler.ConsumerSecret);
            });

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("Store__WooCommerce__ConsumerKey");
    }
}
