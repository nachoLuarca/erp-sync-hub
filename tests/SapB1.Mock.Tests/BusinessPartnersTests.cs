using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class BusinessPartnersTests : IAsyncLifetime, IDisposable
{
    private const string Collection = "/b1s/v1/BusinessPartners";

    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _sessionId = string.Empty;

    public BusinessPartnersTests()
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
        var post = await _client.SendAsync(HttpMethod.Post, Collection, content: JsonBody(new { CardName = "X" }));

        get.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        post.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_con_clave_devuelve_el_socio_con_sus_direcciones()
    {
        var response = await _client.GetWithSessionAsync($"{Collection}('C00001')", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        var partner = body.RootElement;
        partner.GetProperty("odata.metadata").GetString()!.ShouldEndWith("$metadata#BusinessPartners/@Element");
        partner.GetProperty("CardCode").GetString().ShouldBe("C00001");
        partner.GetProperty("CardName").GetString().ShouldBe("Constructora Los Andes SpA");
        partner.GetProperty("CardType").GetString().ShouldBe("cCustomer");
        partner.GetProperty("FederalTaxID").GetString().ShouldBe("76.123.456-0");
        partner.GetProperty("BPAddresses").GetArrayLength().ShouldBe(2);
        partner.GetProperty("BPAddresses")[0].GetProperty("AddressType").GetString().ShouldBe("bo_BillTo");
    }

    [Fact]
    public async Task Get_con_clave_inexistente_responde_404()
    {
        var response = await _client.GetWithSessionAsync($"{Collection}('NO-EXISTE')", _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-2028);
    }

    [Theory]
    [InlineData("CardType eq 'cSupplier'", new[] { "P00001" })]
    [InlineData("FederalTaxID eq '12.345.678-5'", new[] { "C00003" })]
    [InlineData("EmailAddress eq 'taller.rojas@example.com'", new[] { "C00005" })]
    [InlineData("CardType eq 'cCustomer' and startswith(CardName,'Comercial')", new[] { "C00006" })]
    [InlineData("contains(CardName,'Ltda')", new[] { "C00002", "C00006" })]
    [InlineData("FederalTaxID eq '00.000.000-0'", new string[0])]
    public async Task Filter_permite_buscar_socios(string filter, string[] expected)
    {
        var url = $"{Collection}?$filter={Uri.EscapeDataString(filter)}&$select=CardCode";

        var response = await _client.GetWithSessionAsync(url, _sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        CardCodes(body).ShouldBe(expected);
    }

    [Fact]
    public async Task La_coleccion_devuelve_los_siete_socios_semilla()
    {
        var response = await _client.GetWithSessionAsync($"{Collection}?$select=CardCode", _sessionId);

        using var body = await response.ReadJsonAsync();
        CardCodes(body).ShouldBe(["C00001", "C00002", "C00003", "C00004", "C00005", "C00006", "P00001"]);
    }

    [Fact]
    public async Task Las_claves_con_comilla_simple_se_escriben_duplicadas_en_la_URL()
    {
        await Post(new { CardCode = "O'BRIEN", CardName = "Con comilla" });

        var get = await _client.GetWithSessionAsync($"{Collection}('O''BRIEN')", _sessionId);
        var patch = await Patch("O''BRIEN", new { CardName = "Modificado" });

        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await get.ReadJsonAsync();
        body.RootElement.GetProperty("CardCode").GetString().ShouldBe("O'BRIEN");
        patch.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Post_crea_el_socio_con_valores_por_defecto_y_responde_201()
    {
        var response = await Post(new { CardCode = "WEB0001", CardName = "Cliente Web" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("odata.metadata").GetString()!.ShouldEndWith("#BusinessPartners/@Element");
        body.RootElement.GetProperty("CardCode").GetString().ShouldBe("WEB0001");
        body.RootElement.GetProperty("CardType").GetString().ShouldBe("cCustomer");
        body.RootElement.GetProperty("Valid").GetString().ShouldBe("tYES");
        body.RootElement.GetProperty("Frozen").GetString().ShouldBe("tNO");

        var stored = await _client.GetWithSessionAsync($"{Collection}('WEB0001')", _sessionId);
        stored.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_con_datos_completos_los_conserva()
    {
        var response = await Post(new
        {
            CardCode = "WEB0002",
            CardName = "Pedro Pérez",
            CardType = "cCustomer",
            FederalTaxID = "11.111.111-1",
            EmailAddress = "pedro@example.com",
            Phone1 = "+56 9 1111 2222",
            BPAddresses = new[]
            {
                new { AddressName = "Casa", AddressType = "bo_ShipTo", Street = "Calle 1", City = "Santiago", Country = "CL" },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("FederalTaxID").GetString().ShouldBe("11.111.111-1");
        body.RootElement.GetProperty("BPAddresses")[0].GetProperty("Street").GetString().ShouldBe("Calle 1");
    }

    [Fact]
    public async Task Post_sin_CardCode_genera_el_siguiente_de_la_serie()
    {
        var first = await Post(new { CardName = "Uno" });
        var second = await Post(new { CardName = "Dos" });
        var supplier = await Post(new { CardName = "Proveedor", CardType = "cSupplier" });

        using var firstBody = await first.ReadJsonAsync();
        using var secondBody = await second.ReadJsonAsync();
        using var supplierBody = await supplier.ReadJsonAsync();
        firstBody.RootElement.GetProperty("CardCode").GetString().ShouldBe("C00007");
        secondBody.RootElement.GetProperty("CardCode").GetString().ShouldBe("C00008");
        supplierBody.RootElement.GetProperty("CardCode").GetString().ShouldBe("P00002");
    }

    [Fact]
    public async Task Post_con_CardCode_duplicado_responde_400_ya_existe()
    {
        var response = await Post(new { CardCode = "C00001", CardName = "Repetido" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-2035);
        message.ShouldContain("already exists");
    }

    [Theory]
    [InlineData("{ \"CardCode\": \"X1\" }", "CardName")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"  \" }", "CardName")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"A\", \"CardType\": \"cOtro\" }", "CardType")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"A\", \"Valid\": \"si\" }", "Valid")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"A\", \"Frozen\": \"si\" }", "Frozen")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"A\", \"BPAddresses\": [ { \"AddressName\": \"\" } ] }", "AddressName")]
    [InlineData("{ \"CardCode\": \"X1\", \"CardName\": \"A\", \"BPAddresses\": [ { \"AddressName\": \"a\", \"AddressType\": \"otro\" } ] }", "AddressType")]
    public async Task Post_invalido_responde_400_de_validacion(string json, string field)
    {
        var response = await PostRaw(json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-10);
        message.ShouldContain(field);
    }

    [Fact]
    public async Task Post_con_propiedad_inexistente_responde_400_propiedad_invalida()
    {
        var response = await PostRaw("{ \"CardName\": \"A\", \"Foo\": 1 }");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(-1000);
        message.ShouldBe("Property 'Foo' of 'BusinessPartner' is invalid");
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"CardName\": 123 }")]
    public async Task Post_con_cuerpo_mal_formado_responde_400(string json)
    {
        var response = await PostRaw(json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-1);
    }

    [Fact]
    public async Task Patch_modifica_solo_las_propiedades_enviadas_y_responde_204()
    {
        var response = await Patch("C00001", new { EmailAddress = "nuevo@losandes.example.com", Frozen = "tYES" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var stored = await _client.GetWithSessionAsync($"{Collection}('C00001')", _sessionId);
        using var body = await stored.ReadJsonAsync();
        body.RootElement.GetProperty("EmailAddress").GetString().ShouldBe("nuevo@losandes.example.com");
        body.RootElement.GetProperty("Frozen").GetString().ShouldBe("tYES");
        body.RootElement.GetProperty("CardName").GetString().ShouldBe("Constructora Los Andes SpA");
        body.RootElement.GetProperty("BPAddresses").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Patch_acepta_repetir_el_mismo_CardCode()
    {
        var response = await Patch("C00001", new { CardCode = "C00001", CardName = "Nuevo nombre" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Patch_con_CardCode_distinto_responde_400()
    {
        var response = await Patch("C00001", new { CardCode = "OTRO" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-10);
    }

    [Fact]
    public async Task Patch_que_deja_el_socio_invalido_responde_400_y_no_lo_modifica()
    {
        var response = await Patch("C00001", new { CardName = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var stored = await _client.GetWithSessionAsync($"{Collection}('C00001')", _sessionId);
        using var body = await stored.ReadJsonAsync();
        body.RootElement.GetProperty("CardName").GetString().ShouldBe("Constructora Los Andes SpA");
    }

    [Fact]
    public async Task Patch_de_socio_inexistente_responde_404()
    {
        var response = await Patch("NO-EXISTE", new { CardName = "X" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-2028);
    }

    [Fact]
    public async Task Patch_con_propiedad_inexistente_responde_400()
    {
        var response = await Patch("C00001", new { Foo = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadSapErrorAsync()).Code.ShouldBe(-1000);
    }

    [Fact]
    public async Task Reset_restaura_los_datos_semilla()
    {
        await Post(new { CardCode = "WEB0001", CardName = "Cliente Web" });
        await Patch("C00001", new { CardName = "Cambiado" });

        var reset = await _client.PostAsync("/_mock/reset", content: null);

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetWithSessionAsync($"{Collection}('WEB0001')", _sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var restored = await _client.GetWithSessionAsync($"{Collection}('C00001')", _sessionId);
        using var body = await restored.ReadJsonAsync();
        body.RootElement.GetProperty("CardName").GetString().ShouldBe("Constructora Los Andes SpA");
    }

    private Task<HttpResponseMessage> Post(object payload) =>
        _client.SendAsync(HttpMethod.Post, Collection, _sessionId, JsonBody(payload));

    private Task<HttpResponseMessage> PostRaw(string json) =>
        _client.SendAsync(HttpMethod.Post, Collection, _sessionId, new StringContent(json, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> Patch(string cardCode, object payload) =>
        _client.SendAsync(HttpMethod.Patch, $"{Collection}('{cardCode}')", _sessionId, JsonBody(payload));

    // SAP distingue mayúsculas en los nombres de propiedad: sin la política camelCase de los valores por defecto web.
    private static JsonContent JsonBody(object payload) => JsonContent.Create(payload, options: new JsonSerializerOptions());

    private static string[] CardCodes(JsonDocument body) =>
        [.. body.RootElement.GetProperty("value").EnumerateArray().Select(item => item.GetProperty("CardCode").GetString()!)];
}
