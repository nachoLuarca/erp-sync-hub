using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using SapB1.Mock.Configuration;
using SapB1.Mock.Tests.Support;

namespace SapB1.Mock.Tests;

public sealed class AdminKeyTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private readonly WebApplicationFactory<Program> _open = new();
    private readonly WebApplicationFactory<Program> _protected;

    public AdminKeyTests()
    {
        _protected = _open.WithWebHostBuilder(builder => builder.UseSetting("SapMock:AdminApiKey", Key));
    }

    public void Dispose()
    {
        _protected.Dispose();
        _open.Dispose();
    }

    [Fact]
    public async Task Sin_clave_configurada_los_endpoints_del_simulador_quedan_abiertos()
    {
        using var client = _open.CreateClient();

        var response = await client.PostAsync("/_mock/reset", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Con_clave_configurada_se_rechaza_la_peticion_sin_cabecera()
    {
        using var client = _protected.CreateClient();

        var response = await client.PostAsync("/_mock/reset", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Con_clave_configurada_se_rechaza_una_clave_incorrecta()
    {
        using var client = _protected.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key[..^1] + "0");

        var response = await client.PostAsync("/_mock/sessions/expire", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Con_clave_configurada_se_acepta_la_clave_correcta()
    {
        using var client = _protected.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);

        var response = await client.PostAsync("/_mock/reset", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task La_clave_no_afecta_a_la_salud_ni_al_Service_Layer()
    {
        using var client = _protected.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.LoginRawAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("corta", false)]
    [InlineData("0123456789abcdef 0123456789abcdef", false)]
    [InlineData("", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    public void La_clave_debe_tener_entre_32_y_256_caracteres_sin_espacios_o_estar_vacia(string key, bool valid)
    {
        var options = new MockOptions { CompanyDb = "X", UserName = "u", Password = "p", Version = "1", AdminApiKey = key };

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        isValid.ShouldBe(valid, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }
}
