using System.Net;
using SapB1.Client.Models;
using SapB1.Client.Tests.Support;

namespace SapB1.Client.Tests;

public class ItemsApiTests
{
    [Fact]
    public async Task GetAsync_devuelve_el_articulo_con_precios_y_existencias()
    {
        await using var harness = ClientHarness.AgainstMock();

        var item = await harness.Client.Items.GetAsync("A00001");

        item.ShouldNotBeNull();
        item.ItemCode.ShouldBe("A00001");
        item.ItemName.ShouldBe("Taladro percutor 650 W");
        item.Valid.ShouldBe(YesNo.Yes);
        item.Frozen.ShouldBe(YesNo.No);
        item.QuantityOnStock.ShouldBe(45m);
        item.ItemPrices.ShouldHaveSingleItem().ShouldBe(new ItemPrice { PriceList = 1, Price = 39990m, Currency = "CLP" });
        item.ItemWarehouseInfoCollection.ShouldHaveSingleItem()
            .ShouldBe(new ItemWarehouseInfo { WarehouseCode = "01", InStock = 45m, Committed = 0m, Ordered = 0m });
    }

    [Fact]
    public async Task GetAsync_de_un_articulo_inexistente_devuelve_null()
    {
        await using var harness = ClientHarness.AgainstMock();

        var item = await harness.Client.Items.GetAsync("NO-EXISTE");

        item.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_exige_un_codigo()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentException>(() => harness.Client.Items.GetAsync(string.Empty));
    }

    [Fact]
    public async Task QueryAsync_sin_filtros_recorre_todas_las_paginas()
    {
        await using var harness = ClientHarness.AgainstMock(options => options.PageSize = 5);

        var items = await ToListAsync(harness.Client.Items.QueryAsync());

        items.Count.ShouldBe(24);
        items.Select(i => i.ItemCode).ShouldBe(Enumerable.Range(1, 24).Select(n => $"A{n:D5}"));
        var pages = harness.Log.Requests.Where(r => r.PathAndQuery.Contains("Items", StringComparison.Ordinal)).ToList();
        pages.Count.ShouldBe(5);
        pages.ShouldAllBe(page => page.Prefer == "odata.maxpagesize=5");
    }

    [Fact]
    public async Task QueryAsync_con_la_pagina_por_defecto_del_cliente_pide_una_sola_pagina()
    {
        await using var harness = ClientHarness.AgainstMock();

        var items = await ToListAsync(harness.Client.Items.QueryAsync());

        items.Count.ShouldBe(24);
        harness.Log.Requests.Count(r => r.PathAndQuery.Contains("Items", StringComparison.Ordinal)).ShouldBe(1);
        harness.Log.Requests[1].Prefer.ShouldBe("odata.maxpagesize=100");
    }

    [Fact]
    public async Task QueryAsync_aplica_filtro_orden_y_seleccion()
    {
        await using var harness = ClientHarness.AgainstMock();

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery
        {
            Filter = "QuantityOnStock gt 200",
            OrderBy = "QuantityOnStock desc",
            Select = ["ItemCode", "QuantityOnStock"],
        }));

        items.Select(i => i.ItemCode).ShouldBe(["A00022", "A00021", "A00016", "A00010", "A00017", "A00015"]);
        items[0].QuantityOnStock.ShouldBe(600m);
        items.ShouldAllBe(item => item.ItemName == null && item.ItemPrices.Count == 0);
    }

    [Fact]
    public async Task QueryAsync_con_Top_se_detiene_en_el_total_pedido_aunque_abarque_varias_paginas()
    {
        await using var harness = ClientHarness.AgainstMock(options => options.PageSize = 3);

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Top = 7, OrderBy = "ItemCode" }));

        items.Select(i => i.ItemCode).ShouldBe(Enumerable.Range(1, 7).Select(n => $"A{n:D5}"));
        harness.Log.Requests.Count(r => r.PathAndQuery.Contains("Items", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Fact]
    public async Task QueryAsync_con_Skip_omite_filas()
    {
        await using var harness = ClientHarness.AgainstMock();

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Skip = 22 }));

        items.Select(i => i.ItemCode).ShouldBe(["A00023", "A00024"]);
    }

    [Fact]
    public async Task QueryAsync_con_Top_cero_no_hace_peticiones()
    {
        await using var harness = ClientHarness.AgainstMock();

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Top = 0 }));

        items.ShouldBeEmpty();
        harness.Log.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryAsync_sin_resultados_devuelve_una_secuencia_vacia()
    {
        await using var harness = ClientHarness.AgainstMock();

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Filter = SapFilter.Equal("ItemCode", "ZZZ") }));

        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryAsync_no_descarga_paginas_que_no_se_enumeran()
    {
        await using var harness = ClientHarness.AgainstMock(options => options.PageSize = 5);

        await foreach (var item in harness.Client.Items.QueryAsync())
        {
            item.ShouldNotBeNull();
            break;
        }

        harness.Log.Requests.Count(r => r.PathAndQuery.Contains("Items", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task QueryAsync_con_un_filtro_malicioso_no_altera_la_consulta()
    {
        await using var harness = ClientHarness.AgainstMock();
        var injection = "x' or ItemCode ne 'zzz";

        var items = await ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Filter = SapFilter.Equal("ItemName", injection) }));

        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryAsync_con_un_filtro_invalido_lanza_SapB1ApiException_con_el_error_de_SAP()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => ToListAsync(harness.Client.Items.QueryAsync(new SapQuery { Filter = "Inexistente eq 1" })));

        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exception.SapErrorCode.ShouldBe(-1);
        exception.SapErrorMessage.ShouldBe("Property 'Inexistente' is invalid.");
        exception.IsNotFound.ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_cancelada_lanza_OperationCanceledException()
    {
        await using var harness = ClientHarness.AgainstMock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ToListAsync(harness.Client.Items.QueryAsync(cancellationToken: cts.Token)));
    }

    [Fact]
    public async Task Un_404_sin_error_de_SAP_en_GetAsync_lanza_SapB1ApiException()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? LoginOk()
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var exception = await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        exception.SapErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Un_error_del_servidor_lanza_SapB1ApiException_con_el_mensaje_de_SAP()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? LoginOk()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        """{"error":{"code":-5,"message":{"lang":"en-us","value":"Internal error"}}}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });

        var exception = await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        exception.SapErrorCode.ShouldBe(-5);
        exception.SapErrorMessage.ShouldBe("Internal error");
        exception.Message.ShouldContain("Internal error");
    }

    [Fact]
    public async Task Una_respuesta_exitosa_que_no_es_JSON_lanza_SapB1ApiException()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? LoginOk()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>proxy</html>", System.Text.Encoding.UTF8, "application/json"),
                });

        await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Items.GetAsync("A00001"));
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var element in source)
        {
            list.Add(element);
        }

        return list;
    }

    private static HttpResponseMessage LoginOk()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"SessionId":"abc"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("Set-Cookie", "B1SESSION=abc; path=/b1s/v1; HttpOnly");
        return response;
    }
}
