using System.Net;
using System.Text.Json;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class ItemsTests : IAsyncLifetime, IDisposable
{
    private const string AllRows = "odata.maxpagesize=0";

    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _sessionId = string.Empty;

    public ItemsTests()
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
    public async Task Items_sin_sesion_responde_401()
    {
        var response = await _client.SendAsync(HttpMethod.Get, "/b1s/v1/Items");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(301);
    }

    [Fact]
    public async Task Items_con_clave_devuelve_la_entidad_con_el_formato_del_Service_Layer()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        var item = body.RootElement;
        item.GetProperty("odata.metadata").GetString()!.ShouldEndWith("/b1s/v1/$metadata#Items/@Element");
        item.GetProperty("ItemCode").GetString().ShouldBe("A00001");
        item.GetProperty("ItemName").GetString().ShouldBe("Taladro percutor 650 W");
        item.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(45);
        item.GetProperty("Valid").GetString().ShouldBe("tYES");

        var price = item.GetProperty("ItemPrices")[0];
        price.GetProperty("PriceList").GetInt32().ShouldBe(1);
        price.GetProperty("Price").GetDecimal().ShouldBe(39990);
        price.GetProperty("Currency").GetString().ShouldBe("CLP");

        var warehouse = item.GetProperty("ItemWarehouseInfoCollection")[0];
        warehouse.GetProperty("WarehouseCode").GetString().ShouldBe("01");
        warehouse.GetProperty("InStock").GetDecimal().ShouldBe(45);
    }

    [Fact]
    public async Task Items_con_clave_inexistente_responde_404_con_el_error_de_SAP()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items('NO-EXISTE')", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-2028);
        message.ShouldBe("No matching records found (ODBC -2028)");
    }

    [Fact]
    public async Task La_clave_codificada_en_la_URL_tambien_se_resuelve()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items(%27A00002%27)", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task La_coleccion_se_pagina_de_a_20_y_el_nextLink_lleva_al_resto()
    {
        var first = await _client.GetWithSessionAsync("/b1s/v1/Items", _sessionId);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.Contains("Preference-Applied").ShouldBeFalse();
        using var firstPage = await first.ReadJsonAsync();
        firstPage.RootElement.GetProperty("odata.metadata").GetString()!.ShouldEndWith("/b1s/v1/$metadata#Items");
        firstPage.RootElement.GetProperty("value").GetArrayLength().ShouldBe(20);
        var nextLink = firstPage.RootElement.GetProperty("odata.nextLink").GetString();
        nextLink.ShouldBe("Items?$skip=20");

        var second = await _client.GetWithSessionAsync($"/b1s/v1/{nextLink}", _sessionId);

        using var secondPage = await second.ReadJsonAsync();
        secondPage.RootElement.GetProperty("value").GetArrayLength().ShouldBe(4);
        secondPage.RootElement.TryGetProperty("odata.nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Prefer_maxpagesize_cambia_el_tamano_de_pagina_e_informa_Preference_Applied()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items", _sessionId, "odata.maxpagesize=5");

        response.Headers.GetValues("Preference-Applied").ShouldBe(["odata.maxpagesize=5"]);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(5);
        body.RootElement.GetProperty("odata.nextLink").GetString().ShouldBe("Items?$skip=5");
    }

    [Fact]
    public async Task Prefer_maxpagesize_cero_desactiva_la_paginacion()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items", _sessionId, AllRows);

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(24);
        body.RootElement.TryGetProperty("odata.nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Top_limita_las_filas_y_no_genera_nextLink_si_cabe_en_la_pagina()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items?$top=3", _sessionId);

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(3);
        body.RootElement.TryGetProperty("odata.nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Top_mayor_que_la_pagina_conserva_el_resto_en_el_nextLink()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items?$top=15", _sessionId, "odata.maxpagesize=10");

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(10);
        body.RootElement.GetProperty("odata.nextLink").GetString().ShouldBe("Items?$skip=10&$top=5");
    }

    [Fact]
    public async Task Skip_omite_filas()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items?$skip=22", _sessionId);

        using var body = await response.ReadJsonAsync();
        Codes(body).ShouldBe(["A00023", "A00024"]);
    }

    [Fact]
    public async Task Select_devuelve_solo_las_propiedades_pedidas()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items?$select=ItemCode,ItemName&$top=1", _sessionId);

        using var body = await response.ReadJsonAsync();
        var item = body.RootElement.GetProperty("value")[0];
        item.EnumerateObject().Select(p => p.Name).ShouldBe(["ItemCode", "ItemName"]);
    }

    [Fact]
    public async Task Select_tambien_aplica_a_una_entidad_por_clave()
    {
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')?$select=ItemCode", _sessionId);

        using var body = await response.ReadJsonAsync();
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["odata.metadata", "ItemCode"]);
    }

    [Theory]
    [InlineData("ItemCode eq 'A00003'", new[] { "A00003" })]
    [InlineData("ItemCode ne 'A00003' and QuantityOnStock gt 400", new[] { "A00021", "A00022" })]
    [InlineData("QuantityOnStock eq 0", new[] { "A00024" })]
    [InlineData("QuantityOnStock gt 100 and QuantityOnStock lt 200", new[] { "A00007", "A00009", "A00014", "A00018", "A00020", "A00023" })]
    [InlineData("QuantityOnStock ge 400", new[] { "A00016", "A00021", "A00022" })]
    [InlineData("QuantityOnStock le 18", new[] { "A00003", "A00024" })]
    [InlineData("ItemCode eq 'A00001' or ItemCode eq 'A00002'", new[] { "A00001", "A00002" })]
    [InlineData("not (QuantityOnStock gt 10)", new[] { "A00024" })]
    [InlineData("startswith(ItemName,'Cinta')", new[] { "A00016", "A00022" })]
    [InlineData("endswith(ItemName,'LED 9 W')", new[] { "A00021" })]
    [InlineData("endswith(ItemName,'led 9 w')", new string[0])]
    [InlineData("contains(ItemName,'seguridad')", new[] { "A00017", "A00018", "A00019" })]
    [InlineData("substringof('seguridad',ItemName)", new[] { "A00017", "A00018", "A00019" })]
    [InlineData("(ItemCode eq 'A00001' or ItemCode eq 'A00002') and QuantityOnStock gt 50", new[] { "A00002" })]
    [InlineData("ItemName eq 'Candado 40 mm'", new[] { "A00023" })]
    public async Task Filter_selecciona_las_filas_esperadas(string filter, string[] expected)
    {
        var url = $"/b1s/v1/Items?$filter={Uri.EscapeDataString(filter)}&$select=ItemCode";

        var response = await _client.GetWithSessionAsync(url, _sessionId, AllRows);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        Codes(body).ShouldBe(expected);
    }

    [Fact]
    public async Task Filter_acepta_comillas_escapadas_en_los_literales()
    {
        var url = $"/b1s/v1/Items?$filter={Uri.EscapeDataString("ItemName eq 'Brocha 3\"' or ItemName eq 'it''s'")}&$select=ItemCode";

        var response = await _client.GetWithSessionAsync(url, _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        Codes(body).ShouldBe(["A00015"]);
    }

    [Theory]
    [InlineData("Inexistente eq 1")]
    [InlineData("ItemCode eq")]
    [InlineData("ItemCode 'A00001'")]
    [InlineData("ItemCode eq 'sin cerrar")]
    [InlineData("(ItemCode eq 'A00001'")]
    [InlineData("ItemCode eq 'A00001' )")]
    [InlineData("ItemCode eq 'A00001' & QuantityOnStock gt 1")]
    public async Task Filter_invalido_responde_400_con_error_de_SAP(string filter)
    {
        var response = await _client.GetWithSessionAsync($"/b1s/v1/Items?$filter={Uri.EscapeDataString(filter)}", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-1);
        message.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("$select=Inexistente")]
    [InlineData("$orderby=Inexistente")]
    [InlineData("$orderby=ItemCode sideways")]
    [InlineData("$top=abc")]
    [InlineData("$skip=-1")]
    public async Task Opciones_de_consulta_invalidas_responden_400(string query)
    {
        var response = await _client.GetWithSessionAsync($"/b1s/v1/Items?{query}", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-1);
    }

    [Fact]
    public async Task Orderby_ordena_ascendente_y_descendente_y_admite_varias_claves()
    {
        var desc = await _client.GetWithSessionAsync(
            "/b1s/v1/Items?$orderby=QuantityOnStock desc&$top=2&$select=ItemCode", _sessionId);
        var asc = await _client.GetWithSessionAsync(
            "/b1s/v1/Items?$orderby=Valid asc,ItemName&$top=2&$select=ItemCode", _sessionId);

        using var descBody = await desc.ReadJsonAsync();
        using var ascBody = await asc.ReadJsonAsync();
        Codes(descBody).ShouldBe(["A00022", "A00021"]);
        Codes(ascBody).ShouldBe(["A00024", "A00009"]); // "Adhesivo de contacto 250 ml", "Alicate universal 8"""
    }

    [Fact]
    public async Task Inlinecount_informa_el_total_de_filas_que_cumplen_el_filtro()
    {
        var response = await _client.GetWithSessionAsync(
            "/b1s/v1/Items?$filter=QuantityOnStock gt 100&$inlinecount=allpages&$top=1", _sessionId);

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("odata.count").GetString().ShouldBe("13");
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task El_nextLink_conserva_filtro_y_orden()
    {
        var response = await _client.GetWithSessionAsync(
            "/b1s/v1/Items?$filter=QuantityOnStock gt 20&$orderby=ItemCode", _sessionId, "odata.maxpagesize=5");

        using var body = await response.ReadJsonAsync();
        var nextLink = body.RootElement.GetProperty("odata.nextLink").GetString()!;
        nextLink.ShouldStartWith("Items?");
        Uri.UnescapeDataString(nextLink).ShouldContain("$filter=QuantityOnStock gt 20");
        nextLink.ShouldContain("$orderby=ItemCode");
        nextLink.ShouldEndWith("$skip=5");
    }

    [Fact]
    public async Task La_sesion_se_renueva_con_cada_peticion()
    {
        _factory.Time.Advance(TimeSpan.FromMinutes(20));
        (await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        _factory.Time.Advance(TimeSpan.FromMinutes(20));
        var response = await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Con_la_sesion_expirada_los_Items_responden_401()
    {
        _factory.Time.Advance(TimeSpan.FromMinutes(31));

        var response = await _client.GetWithSessionAsync("/b1s/v1/Items", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(301);
    }

    private static string[] Codes(JsonDocument body) =>
        [.. body.RootElement.GetProperty("value").EnumerateArray().Select(item => item.GetProperty("ItemCode").GetString()!)];
}
