using System.Net;
using System.Net.Http.Json;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class FaultsTests : IAsyncLifetime, IDisposable
{
    private const string ItemUrl = "/b1s/v1/Items('A00001')";

    private readonly MockApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _sessionId = string.Empty;

    public FaultsTests()
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

    private Task<HttpResponseMessage> SetFaults(object faults) => _client.PutAsJsonAsync("/_mock/faults", faults);

    private Task<HttpResponseMessage> GetItem() => _client.GetWithSessionAsync(ItemUrl, _sessionId);

    [Fact]
    public async Task Por_defecto_no_hay_fallas_activas()
    {
        var response = await _client.GetAsync("/_mock/faults");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        body.RootElement.GetProperty("Settings").GetProperty("Outage").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("Settings").GetProperty("OutageMode").GetString().ShouldBe("Unavailable");
        body.RootElement.GetProperty("Injected").GetProperty("Errors").GetInt64().ShouldBe(0);
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task La_caida_total_responde_503_con_el_formato_de_error_de_SAP_hasta_que_se_quita()
    {
        (await SetFaults(new { Outage = true })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = await GetItem();
        var login = await _client.LoginRawAsync();

        item.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        login.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var (code, message) = await item.ReadSapErrorAsync();
        code.ShouldBe(-1001);
        message.ShouldContain("unavailable");

        (await _client.DeleteAsync("/_mock/faults")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.LoginRawAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task La_caida_total_no_afecta_a_la_salud_ni_a_la_administracion()
    {
        await SetFaults(new { Outage = true });

        (await _client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/_mock/faults")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync("/_mock/sessions/expire", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task La_caida_con_corte_de_conexion_no_responde()
    {
        await SetFaults(new { Outage = true, OutageMode = "ConnectionReset" });

        await Should.ThrowAsync<Exception>(GetItem);
    }

    [Fact]
    public async Task Los_errores_intermitentes_ocurren_cada_enesima_peticion_con_el_codigo_pedido()
    {
        await SetFaults(new { ErrorEveryNth = 3, ErrorStatusCode = 502 });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            statuses.Add((await GetItem()).StatusCode);
        }

        statuses.ShouldBe(
        [
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadGateway,
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadGateway,
            HttpStatusCode.OK,
        ]);
        using var report = await (await _client.GetAsync("/_mock/faults")).ReadJsonAsync();
        report.RootElement.GetProperty("Injected").GetProperty("Errors").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Con_probabilidad_uno_todas_fallan_y_con_cero_ninguna()
    {
        await SetFaults(new { ErrorRate = 1.0 });
        for (var i = 0; i < 5; i++)
        {
            (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        await SetFaults(new { ErrorRate = 0.0 });
        for (var i = 0; i < 5; i++)
        {
            (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task La_latencia_demora_la_respuesta_hasta_que_pasa_el_tiempo()
    {
        await SetFaults(new { LatencyMs = 5000 });

        var pending = GetItem();
        await Task.Delay(100);
        pending.IsCompleted.ShouldBeFalse();

        for (var i = 0; i < 100 && !pending.IsCompleted; i++)
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        (await pending).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var report = await (await _client.GetAsync("/_mock/faults")).ReadJsonAsync();
        report.RootElement.GetProperty("Injected").GetProperty("Delayed").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task La_expiracion_de_sesion_forzada_invalida_la_sesion_cada_enesima_peticion_autenticada()
    {
        await SetFaults(new { SessionExpiryEveryNth = 2 });

        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
        var expired = await GetItem();

        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await expired.ReadSapErrorAsync()).Code.ShouldBe(301);
        // La sesión se perdió de verdad: seguir usándola también falla, hasta iniciar una nueva.
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _sessionId = await _client.LoginAsync();
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task El_login_no_cuenta_para_la_expiracion_de_sesion()
    {
        await SetFaults(new { SessionExpiryEveryNth = 2 });

        for (var i = 0; i < 4; i++)
        {
            (await _client.LoginRawAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task La_caida_prevalece_sobre_los_errores_intermitentes()
    {
        await SetFaults(new { Outage = true, ErrorEveryNth = 1, ErrorStatusCode = 500 });

        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("""{"LatencyMs":-1}""")]
    [InlineData("""{"LatencyMs":999999}""")]
    [InlineData("""{"ErrorEveryNth":-1}""")]
    [InlineData("""{"ErrorRate":1.5}""")]
    [InlineData("""{"ErrorRate":-0.1}""")]
    [InlineData("""{"ErrorStatusCode":404}""")]
    [InlineData("""{"ErrorStatusCode":600}""")]
    [InlineData("""{"SessionExpiryEveryNth":-3}""")]
    [InlineData("""{"OutageMode":"Inexistente"}""")]
    public async Task Una_configuracion_invalida_se_rechaza_con_400_y_no_cambia_nada(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/_mock/faults", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reemplazar_la_configuracion_reinicia_los_contadores()
    {
        await SetFaults(new { ErrorEveryNth = 2 });
        await GetItem();
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        await SetFaults(new { ErrorEveryNth = 2 });

        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Reset_quita_las_fallas()
    {
        await SetFaults(new { Outage = true });

        (await _client.PostAsync("/_mock/reset", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await GetItem()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
