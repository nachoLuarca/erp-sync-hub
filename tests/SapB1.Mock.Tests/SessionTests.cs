using System.Net;
using Microsoft.Extensions.Options;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;

    public SessionTests()
    {
        _client = _factory.CreateSapClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Login_valido_devuelve_la_sesion_y_la_cookie_B1SESSION()
    {
        var response = await _client.LoginRawAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldContain("application/json");

        using var body = await response.ReadJsonAsync();
        var sessionId = body.RootElement.GetProperty("SessionId").GetString();
        Guid.TryParse(sessionId, out _).ShouldBeTrue();
        body.RootElement.GetProperty("Version").GetString().ShouldBe("1000160");
        body.RootElement.GetProperty("SessionTimeout").GetInt32().ShouldBe(30);
        body.RootElement.GetProperty("odata.metadata").GetString().ShouldEndWith("/b1s/v1/$metadata#B1Sessions/@Element");

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.ShouldContain(c => c.StartsWith($"B1SESSION={sessionId}", StringComparison.Ordinal)
            && c.Contains("path=/b1s/v1", StringComparison.OrdinalIgnoreCase)
            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        cookies.ShouldContain(c => c.StartsWith("ROUTEID=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OtraSociedad", SapTestExtensions.UserName, SapTestExtensions.Password)]
    [InlineData(SapTestExtensions.CompanyDb, "otro", SapTestExtensions.Password)]
    [InlineData(SapTestExtensions.CompanyDb, SapTestExtensions.UserName, "incorrecta")]
    public async Task Login_con_credenciales_invalidas_responde_401_con_error_de_SAP(
        string companyDb, string userName, string password)
    {
        var response = await _client.LoginRawAsync(companyDb, userName, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var (code, _) = await response.ReadSapErrorAsync();
        code.ShouldBe(-304);
    }

    [Fact]
    public async Task Login_con_cuerpo_que_no_es_JSON_responde_400_con_error_de_SAP()
    {
        var content = new StringContent("esto no es json", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/b1s/v1/Login", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (_, message) = await response.ReadSapErrorAsync();
        message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Peticion_sin_sesion_responde_401_con_sesion_invalida()
    {
        var response = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var (code, message) = await response.ReadSapErrorAsync();
        code.ShouldBe(301);
        message.ShouldBe("Invalid session.");
    }

    [Fact]
    public async Task Peticion_con_sesion_desconocida_responde_401()
    {
        var response = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_cierra_la_sesion()
    {
        var sessionId = await _client.LoginAsync();

        var logout = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", sessionId);
        var reuse = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", sessionId);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task La_sesion_expira_tras_30_minutos_de_inactividad()
    {
        var sessionId = await _client.LoginAsync();

        _factory.Time.Advance(TimeSpan.FromMinutes(31));
        var response = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var (code, _) = await response.ReadSapErrorAsync();
        code.ShouldBe(301);
    }

    [Fact]
    public async Task Cada_login_crea_una_sesion_independiente()
    {
        var first = await _client.LoginAsync();
        var second = await _client.LoginAsync();

        second.ShouldNotBe(first);
        await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", first);
        var stillValid = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", second);
        stillValid.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Expirar_sesiones_desde_el_admin_invalida_las_sesiones_abiertas()
    {
        var sessionId = await _client.LoginAsync();

        var expire = await _client.PostAsync("/_mock/sessions/expire", content: null);
        var response = await _client.SendAsync(HttpMethod.Post, "/b1s/v1/Logout", sessionId);

        expire.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_no_requiere_sesion()
    {
        var response = await _client.PostAsync("/_mock/reset", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public void Una_configuracion_invalida_impide_arrancar_el_mock()
    {
        using var factory = new MockApiFactory();
        using var invalid = factory.WithWebHostBuilder(
            builder => builder.UseSetting("SapMock:SessionTimeoutMinutes", "0"));

        Should.Throw<OptionsValidationException>(() => invalid.CreateClient());
    }
}
