using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpSyncHub.UnitTests.Inventory;

public class InventorySyncServiceTests
{
    private readonly FakeSapGateway _sap = new();
    private readonly FakeStoreGateway _store = new();
    private readonly FakeMappingStore _mappings = new();
    private readonly FakeSnapshotStore _snapshots = new();
    private readonly InventorySyncOptions _options = new() { SapPageSize = 100 };

    private static readonly InventorySyncRequest Everything = new(ChangedSince: null);

    private InventorySyncService Service() =>
        new(_sap, _store, _mappings, _snapshots, _options, NullLogger<InventorySyncService>.Instance);

    private Task<InventorySyncReport> RunAsync(InventorySyncRequest? request = null) =>
        Service().SyncAsync(request ?? Everything, CancellationToken.None);

    private void Article(string itemCode, decimal inStock, decimal committed = 0, decimal? price = 10000, bool active = true, bool inStore = true)
    {
        _sap.AddInventoryItem(itemCode, inStock, committed, price, active);
        if (inStore)
        {
            _store.AddProduct(itemCode);
        }
    }

    [Fact]
    public async Task El_stock_es_lo_que_hay_en_mano_menos_lo_comprometido()
    {
        Article("A1", inStock: 45, committed: 5);

        var report = await RunAsync();

        var update = _store.Updates.ShouldHaveSingleItem();
        update.Sku.ShouldBe("A1");
        update.StockQuantity.ShouldBe(40);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 1, Updated: 1, Unchanged: 0, Skipped: 0, Failed: 0));
        report.Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task Suma_solo_los_almacenes_configurados()
    {
        _options.Warehouses = "01, 02";
        _sap.Inventory.Add(new SapInventoryItem(
            "A1",
            true,
            null,
            null,
            [new SapWarehouseStock("01", 10, 2), new SapWarehouseStock("02", 5, 0), new SapWarehouseStock("99", 1000, 0)],
            [new SapItemPrice(1, 100)]));
        _store.AddProduct("A1");

        await RunAsync();

        _store.Updates.ShouldHaveSingleItem().StockQuantity.ShouldBe(13);
    }

    [Fact]
    public async Task El_stock_tiene_tope_en_cero_y_se_redondea_hacia_abajo()
    {
        Article("A1", inStock: 3, committed: 8);
        Article("A2", inStock: 10.9m, committed: 0);

        await RunAsync();

        _store.Updates.Single(u => u.Sku == "A1").StockQuantity.ShouldBe(0);
        _store.Updates.Single(u => u.Sku == "A2").StockQuantity.ShouldBe(10);
    }

    [Fact]
    public async Task Un_articulo_sin_los_almacenes_configurados_queda_sin_stock()
    {
        _options.Warehouses = "02";
        Article("A1", inStock: 50);

        await RunAsync();

        _store.Updates.ShouldHaveSingleItem().StockQuantity.ShouldBe(0);
    }

    [Fact]
    public async Task Un_articulo_inhabilitado_queda_con_stock_cero_pero_no_se_despublica()
    {
        Article("A1", inStock: 50, active: false);

        var report = await RunAsync();

        var update = _store.Updates.ShouldHaveSingleItem();
        update.StockQuantity.ShouldBe(0);
        update.Price.ShouldBe(10000m);
        report.Counts.Updated.ShouldBe(1);
    }

    [Fact]
    public async Task El_precio_sale_de_la_lista_configurada_y_se_redondea_a_los_decimales_pedidos()
    {
        _options.PriceList = 2;
        _options.PriceDecimals = 2;
        _sap.Inventory.Add(new SapInventoryItem(
            "A1",
            true,
            null,
            null,
            [new SapWarehouseStock("01", 1, 0)],
            [new SapItemPrice(1, 999), new SapItemPrice(2, 1234.565m)]));
        _store.AddProduct("A1");

        await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Price.ShouldBe(1234.57m);
    }

    [Fact]
    public async Task Sin_decimales_el_precio_se_redondea_a_pesos_enteros()
    {
        Article("A1", inStock: 1, price: 12990.5m);

        await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Price.ShouldBe(12991m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Un_precio_nulo_o_no_positivo_no_se_envia_pero_el_stock_si(int? price)
    {
        Article("A1", inStock: 7, price: price);

        var report = await RunAsync();

        var update = _store.Updates.ShouldHaveSingleItem();
        update.StockQuantity.ShouldBe(7);
        update.Price.ShouldBeNull();
        report.Counts.Updated.ShouldBe(1);
    }

    [Fact]
    public async Task Un_articulo_con_precio_invalido_y_nada_mas_que_enviar_se_cuenta_como_omitido()
    {
        Article("A1", inStock: 7, price: null);
        await RunAsync();

        var report = await RunAsync();

        report.Counts.ShouldBe(new InventorySyncCounts(Read: 1, Updated: 0, Unchanged: 0, Skipped: 1, Failed: 0));
        _store.UpdateCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lo_que_no_cambio_desde_la_ultima_foto_no_se_envia()
    {
        Article("A1", inStock: 10);
        Article("A2", inStock: 20);
        await RunAsync();
        _store.UpdateCalls.Clear();

        var report = await RunAsync();

        _store.UpdateCalls.ShouldBeEmpty();
        _store.FindCalls.ShouldBe(1); // la segunda pasada ni siquiera consulta la tienda
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 2, Updated: 0, Unchanged: 2, Skipped: 0, Failed: 0));
    }

    [Fact]
    public async Task Solo_se_envia_lo_que_cambio()
    {
        Article("A1", inStock: 10, price: 5000);
        await RunAsync();
        _store.UpdateCalls.Clear();

        _sap.Inventory[0] = _sap.Inventory[0] with { Warehouses = [new SapWarehouseStock("01", 8, 0)] };
        await RunAsync();
        var stockOnly = _store.Updates.ShouldHaveSingleItem();
        stockOnly.StockQuantity.ShouldBe(8);
        stockOnly.Price.ShouldBeNull();
        _store.UpdateCalls.Clear();

        _sap.Inventory[0] = _sap.Inventory[0] with { Prices = [new SapItemPrice(1, 5500)] };
        await RunAsync();
        var priceOnly = _store.Updates.ShouldHaveSingleItem();
        priceOnly.StockQuantity.ShouldBeNull();
        priceOnly.Price.ShouldBe(5500m);
    }

    [Fact]
    public async Task La_foto_solo_se_actualiza_con_lo_que_la_tienda_confirmo()
    {
        Article("A1", inStock: 10, price: 5000);

        await RunAsync();

        var snapshot = _snapshots.Snapshots["A1"];
        snapshot.StockQuantity.ShouldBe(10);
        snapshot.Price.ShouldBe(5000m);
        snapshot.ItemCode.ShouldBe("A1");
    }

    [Fact]
    public async Task Una_lectura_completa_ignora_la_foto_y_la_fecha_de_corte()
    {
        Article("A1", inStock: 10);
        await RunAsync();
        _store.UpdateCalls.Clear();
        _sap.InventoryReads.Clear();

        var report = await RunAsync(new InventorySyncRequest(new DateOnly(2026, 3, 1), Full: true));

        _store.Updates.ShouldHaveSingleItem();
        report.Counts.Updated.ShouldBe(1);
        _sap.InventoryReads.ShouldHaveSingleItem().ChangedSince.ShouldBeNull();
    }

    [Fact]
    public async Task La_fecha_de_corte_llega_a_SAP()
    {
        Article("A1", inStock: 10);

        await RunAsync(new InventorySyncRequest(new DateOnly(2026, 3, 1)));

        _sap.InventoryReads.ShouldHaveSingleItem().ChangedSince.ShouldBe(new DateOnly(2026, 3, 1));
    }

    [Fact]
    public async Task Lee_por_paginas_con_clave_sin_saltar_ni_repetir_articulos()
    {
        _options.SapPageSize = 10;
        for (var n = 1; n <= 25; n++)
        {
            Article($"A{n:D3}", inStock: n);
        }

        var report = await RunAsync();

        report.Counts.ShouldBe(new InventorySyncCounts(Read: 25, Updated: 25, Unchanged: 0, Skipped: 0, Failed: 0));
        _sap.InventoryReads.Select(read => read.AfterItemCode).ShouldBe([null, "A010", "A020"]);
        _sap.InventoryReads.ShouldAllBe(read => read.PageSize == 10);
        _store.Updates.Select(u => u.Sku).Distinct().Count().ShouldBe(25);
    }

    [Fact]
    public async Task Una_pagina_exacta_termina_con_una_lectura_vacia()
    {
        _options.SapPageSize = 2;
        Article("A1", inStock: 1);
        Article("A2", inStock: 1);

        var report = await RunAsync();

        report.Counts.Read.ShouldBe(2);
        _sap.InventoryReads.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Un_articulo_sin_producto_en_la_tienda_se_omite_y_no_se_crea_nada()
    {
        Article("A1", inStock: 10, inStore: false);
        Article("A2", inStock: 10);

        var report = await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Sku.ShouldBe("A2");
        _mappings.Products.Keys.ShouldBe(["A2"]); // al artículo sin producto no se le inventa un mapeo
        _snapshots.Snapshots.Keys.ShouldBe(["A2"]);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 2, Updated: 1, Unchanged: 0, Skipped: 1, Failed: 0));
    }

    [Fact]
    public async Task Un_articulo_que_aun_no_esta_en_la_tienda_se_envia_cuando_aparece()
    {
        Article("A1", inStock: 10, inStore: false);
        await RunAsync();

        _store.AddProduct("A1");
        var report = await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Sku.ShouldBe("A1");
        report.Counts.Updated.ShouldBe(1);
    }

    [Fact]
    public async Task Sin_mapeo_usa_el_ItemCode_como_SKU_y_guarda_el_mapeo_al_encontrarlo()
    {
        Article("A1", inStock: 10);

        await RunAsync();

        _mappings.Products.ShouldBe(new Dictionary<string, string> { ["A1"] = "A1" });
    }

    [Fact]
    public async Task Con_mapeo_usa_el_SKU_mapeado_y_no_el_ItemCode()
    {
        _sap.AddInventoryItem("A00001", inStock: 10, price: 1000);
        _mappings.Products["TAL-650"] = "A00001";
        _store.AddProduct("TAL-650");
        _store.AddProduct("A00001"); // existe un producto con el ItemCode, pero el mapeo manda

        await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Sku.ShouldBe("TAL-650");
    }

    [Fact]
    public async Task Un_articulo_con_varios_SKU_actualiza_a_todos_y_cuenta_una_sola_vez()
    {
        _sap.AddInventoryItem("A00001", inStock: 10, price: 1000);
        _mappings.Products["TAL-650"] = "A00001";
        _mappings.Products["TAL-650-AZUL"] = "A00001";
        _store.AddProduct("TAL-650");
        _store.AddProduct("TAL-650-AZUL");

        var report = await RunAsync();

        _store.Updates.Select(u => u.Sku).Order().ShouldBe(["TAL-650", "TAL-650-AZUL"]);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 1, Updated: 1, Unchanged: 0, Skipped: 0, Failed: 0));
    }

    [Fact]
    public async Task Un_producto_que_la_tienda_rechaza_cuenta_como_fallido_y_no_cambia_su_foto()
    {
        Article("A1", inStock: 10);
        Article("A2", inStock: 20);
        _store.ItemErrors["A1"] = ResultError.Rejected(StoreErrors.Rejected, "El producto está en la papelera.");

        var report = await RunAsync();

        report.Counts.ShouldBe(new InventorySyncCounts(Read: 2, Updated: 1, Unchanged: 0, Skipped: 0, Failed: 1));
        report.Completed.ShouldBeTrue();
        _snapshots.Snapshots.Keys.ShouldBe(["A2"]);
    }

    [Fact]
    public async Task Lo_que_fallo_se_reintenta_en_la_siguiente_ejecucion()
    {
        Article("A1", inStock: 10);
        _store.ItemErrors["A1"] = ResultError.Rejected(StoreErrors.Rejected, "No.");
        await RunAsync();
        _store.ItemErrors.Clear();
        _store.UpdateCalls.Clear();

        var report = await RunAsync();

        _store.Updates.ShouldHaveSingleItem().Sku.ShouldBe("A1");
        report.Counts.Updated.ShouldBe(1);
        _snapshots.Snapshots["A1"].StockQuantity.ShouldBe(10);
    }

    [Fact]
    public async Task Una_falla_pasajera_de_la_tienda_interrumpe_la_ejecucion()
    {
        _options.SapPageSize = 2;
        Article("A1", inStock: 1);
        Article("A2", inStock: 1);
        Article("A3", inStock: 1);
        _store.ItemErrors["A1"] = ResultError.Transient(StoreErrors.Unreachable, "Sin conexión.");
        _store.ItemErrors["A2"] = ResultError.Transient(StoreErrors.Unreachable, "Sin conexión.");

        var report = await RunAsync();

        report.Completed.ShouldBeFalse();
        report.Error!.Code.ShouldBe(StoreErrors.Unreachable);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 2, Updated: 0, Unchanged: 0, Skipped: 0, Failed: 2));
        _sap.InventoryReads.Count.ShouldBe(1); // no sigue leyendo SAP
    }

    [Fact]
    public async Task Si_la_tienda_rechaza_las_credenciales_se_interrumpe_y_todo_lo_pendiente_cuenta_como_fallido()
    {
        Article("A1", inStock: 1);
        Article("A2", inStock: 1);
        _store.UpdateFailure = ResultError.Rejected(StoreErrors.Unauthorized, "Credenciales inválidas.");

        var report = await RunAsync();

        report.Error!.Code.ShouldBe(StoreErrors.Unauthorized);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 2, Updated: 0, Unchanged: 0, Skipped: 0, Failed: 2));
        _snapshots.Snapshots.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_falla_la_busqueda_de_productos_se_interrumpe()
    {
        Article("A1", inStock: 1);
        _store.FindFailure = ResultError.Transient(StoreErrors.Timeout, "La tienda no respondió.");

        var report = await RunAsync();

        report.Error!.Code.ShouldBe(StoreErrors.Timeout);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 1, Updated: 0, Unchanged: 0, Skipped: 0, Failed: 1));
        _store.UpdateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_falla_SAP_se_interrumpe_con_el_error_de_SAP()
    {
        _sap.FailOn(nameof(ISapGateway.GetInventoryPageAsync), ResultError.Transient(SapErrors.Unreachable, "Sin conexión."));

        var report = await RunAsync();

        report.Error!.Code.ShouldBe(SapErrors.Unreachable);
        report.Counts.ShouldBe(default);
    }

    [Fact]
    public async Task Si_SAP_falla_en_una_pagina_posterior_conserva_lo_ya_sincronizado()
    {
        _options.SapPageSize = 1;
        Article("A1", inStock: 1);
        Article("A2", inStock: 1);
        _sap.AfterInventoryRead = reads =>
        {
            if (reads == 1)
            {
                _sap.FailOn(nameof(ISapGateway.GetInventoryPageAsync), ResultError.Transient(SapErrors.Timeout, "Tiempo agotado."));
            }
        };

        var report = await RunAsync();

        report.Error!.Code.ShouldBe(SapErrors.Timeout);
        report.Counts.ShouldBe(new InventorySyncCounts(Read: 1, Updated: 1, Unchanged: 0, Skipped: 0, Failed: 0));
        _snapshots.Snapshots.Keys.ShouldBe(["A1"]);
    }

    [Fact]
    public async Task Sin_articulos_no_hace_nada()
    {
        var report = await RunAsync();

        report.Counts.ShouldBe(default);
        report.Completed.ShouldBeTrue();
        _store.FindCalls.ShouldBe(0);
    }

    [Fact]
    public async Task La_cancelacion_se_propaga()
    {
        Article("A1", inStock: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => Service().SyncAsync(Everything, cts.Token));
    }
}
