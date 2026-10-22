using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class OrdersTests : IAsyncLifetime, IDisposable
{
    private const string Collection = "/b1s/v1/Orders";

    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _sessionId = string.Empty;

    public OrdersTests()
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
    public async Task Sin_sesion_responde_401()
    {
        var get = await _client.SendAsync(HttpMethod.Get, Collection);
        var post = await _client.SendAsync(HttpMethod.Post, Collection, content: JsonBody(new { CardCode = "C00001" }));

        get.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        post.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_crea_la_orden_y_calcula_los_datos_que_asigna_SAP()
    {
        var response = await Post(new
        {
            CardCode = "C00001",
            NumAtCard = "WOO-1001",
            Comments = "Pedido web",
            DocumentLines = new object[]
            {
                new { ItemCode = "A00001", Quantity = 2 },
                new { ItemCode = "A00010", Quantity = 3, UnitPrice = 2500 },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await response.ReadJsonAsync();
        var order = body.RootElement;
        order.GetProperty("odata.metadata").GetString()!.ShouldEndWith("#Orders/@Element");
        order.GetProperty("DocEntry").GetInt32().ShouldBe(1);
        order.GetProperty("DocNum").GetInt32().ShouldBe(1001);
        order.GetProperty("DocType").GetString().ShouldBe("dDocument_Items");
        order.GetProperty("CardCode").GetString().ShouldBe("C00001");
        order.GetProperty("CardName").GetString().ShouldBe("Constructora Los Andes SpA");
        order.GetProperty("NumAtCard").GetString().ShouldBe("WOO-1001");
        order.GetProperty("Comments").GetString().ShouldBe("Pedido web");
        order.GetProperty("DocDate").GetString().ShouldBe("2026-01-15");
        order.GetProperty("DocDueDate").GetString().ShouldBe("2026-01-15");
        order.GetProperty("DocCurrency").GetString().ShouldBe("CLP");
        order.GetProperty("DocumentStatus").GetString().ShouldBe("bost_Open");
        order.GetProperty("Cancelled").GetString().ShouldBe("tNO");
        order.GetProperty("DocTotal").GetDecimal().ShouldBe(87480m);

        var lines = order.GetProperty("DocumentLines");
        lines.GetArrayLength().ShouldBe(2);
        var first = lines[0];
        first.GetProperty("LineNum").GetInt32().ShouldBe(0);
        first.GetProperty("ItemCode").GetString().ShouldBe("A00001");
        first.GetProperty("ItemDescription").GetString().ShouldBe("Taladro percutor 650 W");
        first.GetProperty("Quantity").GetDecimal().ShouldBe(2m);
        first.GetProperty("UnitPrice").GetDecimal().ShouldBe(39990m);
        first.GetProperty("LineTotal").GetDecimal().ShouldBe(79980m);
        first.GetProperty("WarehouseCode").GetString().ShouldBe("01");
        first.GetProperty("LineStatus").GetString().ShouldBe("bost_Open");
        lines[1].GetProperty("LineNum").GetInt32().ShouldBe(1);
        lines[1].GetProperty("UnitPrice").GetDecimal().ShouldBe(2500m);
        lines[1].GetProperty("LineTotal").GetDecimal().ShouldBe(7500m);
    }

    [Fact]
    public async Task Post_respeta_las_fechas_enviadas()
    {
        var response = await Post(new
        {
            CardCode = "C00001",
            DocDate = "2026-02-01",
            DocDueDate = "2026-02-10",
            DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1 } },
        });

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("DocDate").GetString().ShouldBe("2026-02-01");
        body.RootElement.GetProperty("DocDueDate").GetString().ShouldBe("2026-02-10");
    }

    [Fact]
    public async Task Post_ignora_lo_que_el_cliente_envia_en_las_propiedades_calculadas()
    {
        var response = await Post(new
        {
            DocEntry = 99,
            DocNum = 5,
            DocTotal = 1,
            CardName = "Otro nombre",
            CardCode = "C00002",
            DocumentLines = new[] { new { ItemCode = "A00010", Quantity = 1, LineTotal = 1, LineNum = 7 } },
        });

        using var body = await response.ReadJsonAsync();
        var order = body.RootElement;
        order.GetProperty("DocEntry").GetInt32().ShouldBe(1);
        order.GetProperty("DocNum").GetInt32().ShouldBe(1001);
        order.GetProperty("CardName").GetString().ShouldBe("Ferretería El Maestro Ltda.");
        order.GetProperty("DocTotal").GetDecimal().ShouldBe(2990m);
        order.GetProperty("DocumentLines")[0].GetProperty("LineNum").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Las_ordenes_siguientes_incrementan_DocEntry_y_DocNum()
    {
        await Post(ValidOrder());
        var second = await Post(ValidOrder());

        using var body = await second.ReadJsonAsync();
        body.RootElement.GetProperty("DocEntry").GetInt32().ShouldBe(2);
        body.RootElement.GetProperty("DocNum").GetInt32().ShouldBe(1002);
    }

    [Fact]
    public async Task Get_con_clave_devuelve_la_orden()
    {
        await Post(ValidOrder("WOO-1"));

        var response = await _client.GetWithSessionAsync($"{Collection}(1)", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("NumAtCard").GetString().ShouldBe("WOO-1");
        body.RootElement.GetProperty("DocumentLines").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Get_con_clave_inexistente_responde_404()
    {
        var response = await _client.GetWithSessionAsync($"{Collection}(1)", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-2028);
    }

    [Fact]
    public async Task La_coleccion_parte_vacia()
    {
        var response = await _client.GetWithSessionAsync(Collection, _sessionId);

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("$filter=NumAtCard eq 'WOO-1'")]
    [InlineData("$filter=NumAtCard eq 'WOO-1'&$orderby=DocEntry desc&$select=DocEntry&$top=1")]
    [InlineData("$inlinecount=allpages")]
    public async Task Consultar_una_coleccion_vacia_con_opciones_responde_200_sin_filas(string query)
    {
        var response = await _client.GetWithSessionAsync($"{Collection}?{query}", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Un_filtro_con_sintaxis_invalida_sobre_una_coleccion_vacia_igual_responde_400()
    {
        var response = await _client.GetWithSessionAsync($"{Collection}?$filter=NumAtCard eq", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filter_por_NumAtCard_permite_detectar_pedidos_ya_enviados()
    {
        await Post(ValidOrder("WOO-1"));
        await Post(ValidOrder("WOO-2"));

        var url = $"{Collection}?$filter={Uri.EscapeDataString("NumAtCard eq 'WOO-2'")}&$select=DocEntry,NumAtCard";
        var response = await _client.GetWithSessionAsync(url, _sessionId);

        using var body = await response.ReadJsonAsync();
        var rows = body.RootElement.GetProperty("value");
        rows.GetArrayLength().ShouldBe(1);
        rows[0].GetProperty("DocEntry").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Orderby_permite_obtener_las_ordenes_mas_recientes_primero()
    {
        await Post(ValidOrder("WOO-1"));
        await Post(ValidOrder("WOO-2"));

        var response = await _client.GetWithSessionAsync($"{Collection}?$orderby=DocEntry desc&$select=DocEntry", _sessionId);

        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("value").EnumerateArray()
            .Select(row => row.GetProperty("DocEntry").GetInt32())
            .ShouldBe([2, 1]);
    }

    [Fact]
    public async Task La_orden_compromete_stock_sin_descontar_el_stock_fisico()
    {
        await Post(new { CardCode = "C00001", DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 2 } } });
        await Post(new { CardCode = "C00001", DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 3 } } });

        var response = await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId);

        using var body = await response.ReadJsonAsync();
        var warehouse = body.RootElement.GetProperty("ItemWarehouseInfoCollection")[0];
        warehouse.GetProperty("Committed").GetDecimal().ShouldBe(5m);
        warehouse.GetProperty("InStock").GetDecimal().ShouldBe(45m);
        body.RootElement.GetProperty("QuantityOnStock").GetDecimal().ShouldBe(45m);
    }

    [Fact]
    public async Task Una_linea_invalida_no_crea_la_orden_ni_compromete_stock()
    {
        var response = await Post(new
        {
            CardCode = "C00001",
            DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 2 }, new { ItemCode = "NO-EXISTE", Quantity = 1 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var orders = await (await _client.GetWithSessionAsync(Collection, _sessionId)).ReadJsonAsync();
        orders.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
        using var item = await (await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId)).ReadJsonAsync();
        item.RootElement.GetProperty("ItemWarehouseInfoCollection")[0].GetProperty("Committed").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Reset_borra_las_ordenes_reinicia_la_numeracion_y_libera_el_stock_comprometido()
    {
        await Post(ValidOrder());

        var reset = await _client.PostAsync("/_mock/reset", content: null);
        var again = await Post(ValidOrder());

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var body = await again.ReadJsonAsync();
        body.RootElement.GetProperty("DocEntry").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("DocNum").GetInt32().ShouldBe(1001);
        using var item = await (await _client.GetWithSessionAsync("/b1s/v1/Items('A00001')", _sessionId)).ReadJsonAsync();
        item.RootElement.GetProperty("ItemWarehouseInfoCollection")[0].GetProperty("Committed").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task Se_aceptan_los_largos_maximos_de_NumAtCard_y_Comments()
    {
        var response = await Post(new
        {
            CardCode = "C00001",
            NumAtCard = new string('n', 100),
            Comments = new string('c', 254),
            DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("{ \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1 } ] }", "CardCode")]
    [InlineData("{ \"CardCode\": \"NO-EXISTE\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1 } ] }", "Business partner 'NO-EXISTE' does not exist")]
    [InlineData("{ \"CardCode\": \"P00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1 } ] }", "vendor")]
    [InlineData("{ \"CardCode\": \"C00001\" }", "DocumentLines")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [] }", "DocumentLines")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ null ] }", "(line 0)")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"Quantity\": 1 } ] }", "(line 0)")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"ZZZ\", \"Quantity\": 1 } ] }", "Item 'ZZZ' does not exist (line 0)")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 0 } ] }", "Quantity must be greater than zero (line 0)")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": -1 } ] }", "Quantity must be greater than zero")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\" } ] }", "Quantity must be greater than zero")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1, \"UnitPrice\": -5 } ] }", "UnitPrice cannot be negative")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1, \"WarehouseCode\": \"99\" } ] }", "Warehouse '99' is not valid for item 'A00001'")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": 1 }, { \"ItemCode\": \"A00002\", \"Quantity\": 0 } ] }", "(line 1)")]
    public async Task Post_invalido_responde_400_de_validacion(string json, string expectedMessage)
    {
        var response = await PostRaw(json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-10);
        message.ShouldContain(expectedMessage);
    }

    [Theory]
    [InlineData(101, 0, "NumAtCard")]
    [InlineData(0, 255, "Comments")]
    public async Task Post_con_textos_demasiado_largos_responde_400(int numAtCardLength, int commentsLength, string field)
    {
        var response = await Post(new
        {
            CardCode = "C00001",
            NumAtCard = numAtCardLength > 0 ? new string('n', numAtCardLength) : null,
            Comments = commentsLength > 0 ? new string('c', commentsLength) : null,
            DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-10);
        message.ShouldContain(field);
    }

    [Fact]
    public async Task Post_a_un_cliente_congelado_responde_400()
    {
        await _client.SendAsync(
            HttpMethod.Patch,
            "/b1s/v1/BusinessPartners('C00001')",
            _sessionId,
            JsonBody(new { Frozen = "tYES" }));

        var response = await Post(ValidOrder());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Message.ShouldContain("inactive");
    }

    [Fact]
    public async Task Post_con_propiedad_inexistente_responde_400_propiedad_invalida()
    {
        var response = await PostRaw("{ \"CardCode\": \"C00001\", \"Foo\": 1 }");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-1000);
        message.ShouldBe("Property 'Foo' of 'Order' is invalid");
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("[1]")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocDate\": \"31/01/2026\" }")]
    [InlineData("{ \"CardCode\": \"C00001\", \"DocumentLines\": [ { \"ItemCode\": \"A00001\", \"Quantity\": \"muchos\" } ] }")]
    public async Task Post_con_cuerpo_mal_formado_responde_400(string json)
    {
        var response = await PostRaw(json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-1);
    }

    private static object ValidOrder(string? numAtCard = null) => new
    {
        CardCode = "C00001",
        NumAtCard = numAtCard,
        DocumentLines = new[] { new { ItemCode = "A00001", Quantity = 1 } },
    };

    private Task<HttpResponseMessage> Post(object payload) =>
        _client.SendAsync(HttpMethod.Post, Collection, _sessionId, JsonBody(payload));

    private Task<HttpResponseMessage> PostRaw(string json) =>
        _client.SendAsync(HttpMethod.Post, Collection, _sessionId, new StringContent(json, Encoding.UTF8, "application/json"));

    // SAP distingue mayúsculas en los nombres de propiedad: sin la política camelCase de los valores por defecto web.
    private static JsonContent JsonBody(object payload) => JsonContent.Create(payload, options: new JsonSerializerOptions());
}
