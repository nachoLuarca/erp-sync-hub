using System.Net;
using System.Text;
using SapB1.Client.Tests.Support;

namespace SapB1.Client.Tests;

public class SessionTests
{
    [Fact]
    public async Task La_primera_llamada_inicia_sesion_automaticamente()
    {
        await using var harness = ClientHarness.AgainstMock();

        var item = await harness.Client.Items.GetAsync("A00001");

        item.ShouldNotBeNull();
        harness.Log.Sequence.ShouldBe(
        [
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 200",
        ]);
    }

    [Fact]
    public async Task Las_llamadas_siguientes_reutilizan_la_sesion()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.Items.GetAsync("A00001");
        await harness.Client.Items.GetAsync("A00002");
        await harness.Client.Items.GetAsync("A00003");

        harness.Log.Logins.ShouldBe(1);
        harness.Log.Requests.Skip(1).Select(r => r.Cookie).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task La_cookie_B1SESSION_viaja_en_cada_peticion_y_no_en_el_login()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.Items.GetAsync("A00001");

        var requests = harness.Log.Requests;
        requests[0].Cookie.ShouldBeNull();
        requests[1].Cookie.ShouldNotBeNull().ShouldStartWith("B1SESSION=");
    }

    [Fact]
    public async Task Renueva_la_sesion_expirada_y_reintenta_la_peticion()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");

        await harness.ExpireSessionsAsync();
        var item = await harness.Client.Items.GetAsync("A00002");

        item.ShouldNotBeNull().ItemCode.ShouldBe("A00002");
        harness.Log.Sequence.ShouldBe(
        [
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 200",
            "GET /b1s/v1/Items('A00002') -> 401",
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00002') -> 200",
        ]);
    }

    [Fact]
    public async Task Tras_renovar_la_sesion_las_siguientes_llamadas_ya_no_fallan()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();
        await harness.Client.Items.GetAsync("A00002");

        await harness.Client.Items.GetAsync("A00003");

        harness.Log.Logins.ShouldBe(2);
        harness.Log.Requests[^1].Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Con_peticiones_concurrentes_y_sesion_expirada_solo_hay_un_login_nuevo()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();

        var items = await Task.WhenAll(
            Enumerable.Range(1, 20).Select(i => harness.Client.Items.GetAsync($"A{i:D5}")));

        items.ShouldAllBe(item => item != null);
        harness.Log.Logins.ShouldBe(2);
    }

    [Fact]
    public async Task Con_peticiones_concurrentes_sin_sesion_previa_solo_hay_un_login()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => harness.Client.Items.GetAsync($"A{i:D5}")));

        harness.Log.Logins.ShouldBe(1);
    }

    [Fact]
    public async Task Credenciales_invalidas_lanzan_SapB1SessionException_con_el_error_de_SAP()
    {
        await using var harness = ClientHarness.AgainstMock(options => options.Password = "incorrecta");

        var exception = await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.SapErrorCode.ShouldBe(-304);
        exception.SapErrorMessage.ShouldNotBeNullOrWhiteSpace();
        exception.Message.ShouldContain("-304");
    }

    [Fact]
    public async Task Un_login_fallido_no_se_recuerda_y_se_reintenta_en_la_llamada_siguiente()
    {
        await using var harness = ClientHarness.AgainstMock(options => options.Password = "incorrecta");

        await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));
        await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));

        harness.Log.Logins.ShouldBe(2);
        harness.Log.Requests.ShouldAllBe(request => request.IsLogin);
    }

    [Fact]
    public async Task Si_SAP_sigue_rechazando_la_sesion_tras_renovarla_lanza_SapB1SessionException_sin_reintentar_mas()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? LoginOk("B1SESSION=abc")
                : Json(HttpStatusCode.Unauthorized, """{"error":{"code":301,"message":{"lang":"en-us","value":"Invalid session."}}}"""));

        var exception = await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.SapErrorCode.ShouldBe(SapB1ErrorCodes.InvalidSession);
        harness.Log.Sequence.ShouldBe(
        [
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 401",
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 401",
        ]);
    }

    [Fact]
    public async Task Si_el_login_no_trae_cookie_usa_el_SessionId_del_cuerpo()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"SessionId":"sesion-123","SessionTimeout":30}""")
                : Json(HttpStatusCode.OK, """{"ItemCode":"A00001"}"""));

        await harness.Client.Items.GetAsync("A00001");

        harness.Log.Requests[1].Cookie.ShouldBe("B1SESSION=sesion-123");
    }

    [Fact]
    public async Task Conserva_tambien_la_cookie_ROUTEID_del_balanceador()
    {
        await using var harness = ClientHarness.AgainstStub(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal)
                ? LoginOk("B1SESSION=abc", "ROUTEID=.node2")
                : Json(HttpStatusCode.OK, """{"ItemCode":"A00001"}"""));

        await harness.Client.Items.GetAsync("A00001");

        harness.Log.Requests[1].Cookie.ShouldBe("B1SESSION=abc; ROUTEID=.node2");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"SessionId":""}""")]
    public async Task Un_login_exitoso_sin_sesion_lanza_SapB1SessionException(string body)
    {
        await using var harness = ClientHarness.AgainstStub(_ => Json(HttpStatusCode.OK, body));

        var exception = await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.Message.ShouldContain("no session");
    }

    [Fact]
    public async Task Un_login_con_respuesta_que_no_es_JSON_lanza_SapB1SessionException()
    {
        await using var harness = ClientHarness.AgainstStub(_ => Json(HttpStatusCode.OK, "<html>proxy</html>"));

        await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));
    }

    [Fact]
    public async Task Un_login_rechazado_por_el_servidor_sin_cuerpo_de_SAP_igual_lanza_SapB1SessionException()
    {
        await using var harness = ClientHarness.AgainstStub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await Should.ThrowAsync<SapB1SessionException>(() => harness.Client.Items.GetAsync("A00001"));

        exception.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        exception.SapErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task LogoutAsync_cierra_la_sesion_y_la_siguiente_llamada_inicia_otra()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");

        await harness.Client.LogoutAsync();
        await harness.Client.Items.GetAsync("A00001");

        harness.Log.Sequence.ShouldBe(
        [
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 200",
            "POST /b1s/v1/Logout -> 204",
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 200",
        ]);
    }

    [Fact]
    public async Task LogoutAsync_sin_sesion_abierta_no_hace_ninguna_peticion()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.LogoutAsync();

        harness.Log.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LogoutAsync_con_la_sesion_ya_expirada_no_falla_ni_inicia_otra_sesion()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();

        await harness.Client.LogoutAsync();

        harness.Log.Sequence.ShouldBe(
        [
            "POST /b1s/v1/Login -> 200",
            "GET /b1s/v1/Items('A00001') -> 200",
            "POST /b1s/v1/Logout -> 401",
        ]);
    }

    [Fact]
    public async Task Una_peticion_cancelada_lanza_OperationCanceledException()
    {
        await using var harness = ClientHarness.AgainstMock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => harness.Client.Items.GetAsync("A00001", cts.Token));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage LoginOk(params string[] cookies)
    {
        var response = Json(HttpStatusCode.OK, """{"SessionId":"abc","Version":"1000160","SessionTimeout":30}""");
        foreach (var cookie in cookies)
        {
            response.Headers.Add("Set-Cookie", $"{cookie}; path=/b1s/v1; HttpOnly");
        }

        return response;
    }
}
