using ErpSyncHub.Application.Sap;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Sap;

/// <summary>La lectura incremental y paginada del maestro de artículos contra el SAP simulado.</summary>
public class SapGatewayInventoryTests
{
    [Fact]
    public async Task Lee_stock_por_almacen_y_precios_por_lista()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.GetInventoryPageAsync(null, null, 1, CancellationToken.None);

        var item = result.Value.ShouldHaveSingleItem();
        item.ItemCode.ShouldBe("A00001");
        item.IsActive.ShouldBeTrue();
        item.UpdatedOn.ShouldNotBeNull();
        item.UpdatedAt.ShouldNotBeNull();
        item.Warehouses.ShouldBe([new SapWarehouseStock("01", 45m, 0m)]);
        item.Prices.ShouldBe([new SapItemPrice(1, 39990m)]);
    }

    [Fact]
    public async Task Pagina_por_clave_sin_saltar_ni_repetir_articulos()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var codes = new List<string>();
        string? after = null;

        while (true)
        {
            var page = (await harness.Gateway.GetInventoryPageAsync(null, after, 10, CancellationToken.None)).Value;
            codes.AddRange(page.Select(item => item.ItemCode));
            if (page.Count < 10)
            {
                break;
            }

            after = page[^1].ItemCode;
        }

        codes.ShouldBe(Enumerable.Range(1, 24).Select(n => $"A{n:D5}"));
    }

    [Fact]
    public async Task Una_pagina_exacta_termina_con_una_pagina_vacia()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var last = await harness.Gateway.GetInventoryPageAsync(null, "A00024", 5, CancellationToken.None);

        last.IsSuccess.ShouldBeTrue();
        last.Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Con_changedSince_trae_solo_los_articulos_modificados_desde_esa_fecha()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var all = await harness.Gateway.GetInventoryPageAsync(today.AddDays(-1), null, 100, CancellationToken.None);
        var none = await harness.Gateway.GetInventoryPageAsync(today.AddDays(1), null, 100, CancellationToken.None);

        all.Value.Count.ShouldBe(24);
        none.Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refleja_los_cambios_de_stock_precio_y_congelado_hechos_en_SAP()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        await harness.Mock.AdjustItemAsync("A00002", new { InStock = 12, Committed = 5, Price = 31990, Frozen = true });

        var item = (await harness.Gateway.GetInventoryPageAsync(null, "A00001", 1, CancellationToken.None)).Value.ShouldHaveSingleItem();

        item.ItemCode.ShouldBe("A00002");
        item.IsActive.ShouldBeFalse();
        item.Warehouses.ShouldBe([new SapWarehouseStock("01", 12m, 5m)]);
        item.Prices.ShouldBe([new SapItemPrice(1, 31990m)]);
    }

    [Fact]
    public async Task Exige_un_tamano_de_pagina_positivo()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => harness.Gateway.GetInventoryPageAsync(null, null, 0, CancellationToken.None));
    }
}
