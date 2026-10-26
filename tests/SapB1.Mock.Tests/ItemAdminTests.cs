using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class ItemAdminTests : IAsyncLifetime, IDisposable
{
    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _sessionId = string.Empty;

    public ItemAdminTests()
    {
        _client = _factory.CreateSapClient();
    }

    public async Task InitializeAsync() => _sessionId = await _client.LoginAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Cambia_stock_y_precio_y_actualiza_la_fecha_de_modificacion()
    {
        (await GetItemAsync("A00001")).Dispose(); // siembra el maestro con el reloj en su hora inicial
        _factory.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(3));
        _sessionId = await _client.LoginAsync();

        var response = await Patch("A00001", new { InStock = 10, Committed = 4, Price = 41990 });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var item = await GetItemAsync("A00001");
        var root = item.RootElement;
        var warehouse = root.GetProperty("ItemWarehouseInfoCollection")[0];
        warehouse.GetProperty("InStock").GetDecimal().ShouldBe(10m);
        warehouse.GetProperty("Committed").GetDecimal().ShouldBe(4m);
        root.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(10m);
        root.GetProperty("ItemPrices")[0].GetProperty("Price").GetDecimal().ShouldBe(41990m);
        root.GetProperty("UpdateDate").GetString().ShouldBe("2026-01-16");
        root.GetProperty("UpdateTime").GetString().ShouldBe("12:00:00");
    }

    [Fact]
    public async Task Los_campos_omitidos_no_se_tocan()
    {
        await Patch("A00001", new { Price = 1000 });

        using var item = await GetItemAsync("A00001");
        item.RootElement.GetProperty("ItemWarehouseInfoCollection")[0].GetProperty("InStock").GetDecimal().ShouldBe(45m);
        item.RootElement.GetProperty("ItemPrices")[0].GetProperty("Price").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public async Task Crea_el_almacen_y_la_lista_de_precios_que_el_articulo_no_tenia()
    {
        await Patch("A00001", new { WarehouseCode = "02", InStock = 7, PriceList = 2, Price = 35000 });

        using var item = await GetItemAsync("A00001");
        var warehouses = item.RootElement.GetProperty("ItemWarehouseInfoCollection");
        warehouses.GetArrayLength().ShouldBe(2);
        warehouses[1].GetProperty("WarehouseCode").GetString().ShouldBe("02");
        item.RootElement.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(52m);
        var price = item.RootElement.GetProperty("ItemPrices")[1];
        price.GetProperty("PriceList").GetInt32().ShouldBe(2);
        price.GetProperty("Price").GetDecimal().ShouldBe(35000m);
    }

    [Fact]
    public async Task Congela_y_descongela_el_articulo()
    {
        await Patch("A00001", new { Frozen = true });
        using (var frozen = await GetItemAsync("A00001"))
        {
            frozen.RootElement.GetProperty("Frozen").GetString().ShouldBe("tYES");
        }

        await Patch("A00001", new { Frozen = false });
        using var thawed = await GetItemAsync("A00001");
        thawed.RootElement.GetProperty("Frozen").GetString().ShouldBe("tNO");
    }

    [Fact]
    public async Task Un_articulo_inexistente_responde_404()
    {
        (await Patch("NO-EXISTE", new { InStock = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("InStock")]
    [InlineData("Committed")]
    [InlineData("Price")]
    public async Task Un_valor_negativo_responde_400_y_no_cambia_nada(string property)
    {
        var response = await Patch("A00001", new Dictionary<string, object> { [property] = -1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var item = await GetItemAsync("A00001");
        item.RootElement.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(45m);
    }

    [Fact]
    public async Task Reset_deshace_los_cambios()
    {
        await Patch("A00001", new { InStock = 1 });

        (await _client.PostAsync("/_mock/reset", content: null)).EnsureSuccessStatusCode();

        using var item = await GetItemAsync("A00001");
        item.RootElement.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(45m);
    }

    private Task<HttpResponseMessage> Patch(string itemCode, object body) =>
        _client.PatchAsJsonAsync($"/_mock/items/{itemCode}", body);

    private async Task<JsonDocument> GetItemAsync(string itemCode) =>
        await (await _client.GetWithSessionAsync($"/b1s/v1/Items('{itemCode}')", _sessionId)).ReadJsonAsync();
}
