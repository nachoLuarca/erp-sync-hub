using System.Net;
using SapB1.Client.Tests.Support;

namespace SapB1.Client.Tests;

/// <summary>Cómo se comporta el cliente cuando el mock simula fallas del Service Layer.</summary>
public class FaultsTests
{
    [Fact]
    public async Task Una_caida_total_se_informa_como_error_del_servidor_503()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.SetFaultsAsync(new { Outage = true });

        var failure = await Should.ThrowAsync<SapB1Exception>(() => harness.Client.Items.GetAsync("A00001"));

        failure.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Un_error_5xx_intermitente_no_se_reintenta_dentro_del_cliente()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.SetFaultsAsync(new { ErrorEveryNth = 1, ErrorStatusCode = 502 });

        var failure = await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Items.GetAsync("A00002"));

        failure.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        harness.Log.Requests.Count(request => request.PathAndQuery.Contains("Items('A00002')", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Con_expiracion_de_sesion_forzada_el_cliente_renueva_la_sesion_y_no_falla()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.SetFaultsAsync(new { SessionExpiryEveryNth = 2 });

        for (var n = 1; n <= 6; n++)
        {
            var item = await harness.Client.Items.GetAsync($"A{n:D5}");
            item.ShouldNotBeNull().ItemCode.ShouldBe($"A{n:D5}");
        }

        harness.Log.Logins.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Despues_de_la_caida_el_cliente_vuelve_a_funcionar_sin_reiniciarse()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.SetFaultsAsync(new { Outage = true });
        await Should.ThrowAsync<SapB1Exception>(() => harness.Client.Items.GetAsync("A00002"));

        await harness.SetFaultsAsync(new { });
        var item = await harness.Client.Items.GetAsync("A00002");

        item.ShouldNotBeNull().ItemCode.ShouldBe("A00002");
    }
}
