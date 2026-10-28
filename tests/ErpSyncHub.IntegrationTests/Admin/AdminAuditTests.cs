using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Core;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>Las acciones manuales del panel quedan auditadas con la huella de la clave, y el registro es de solo inserción.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class AdminAuditTests(PostgresFixture postgres)
{
    private static string Fingerprint(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    private Task<WebhookHost> StartAsync(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        WebhookHost.StartAsync(postgres, builder =>
        {
            builder.UseSetting("Admin:ReadApiKeys", ApiFactory.ReadApiKey);
            configure?.Invoke(builder);
        });

    private static async Task<Guid> InsertFailedJobAsync(WebhookHost host, string externalId)
    {
        var id = Guid.NewGuid();
        await host.ExecuteAsync(
            "INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, created_at, updated_at) " +
            $"VALUES ('{id}', 'woocommerce', '{externalId}', 'order', '{{}}', 'failed', 1, now(), now())");
        return id;
    }

    private static async Task<JsonDocument> ReadAuditAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/admin/audit{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reintentar_un_job_deja_una_entrada_con_la_accion_el_objeto_el_resultado_y_la_huella_de_la_clave()
    {
        await using var host = await StartAsync();
        var jobId = await InsertFailedJobAsync(host, "7001");

        (await host.Client.PostAsync($"/admin/jobs/{jobId}/retry", content: null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var audit = await ReadAuditAsync(host.Client);
        var entry = audit.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("action").GetString().ShouldBe("job.retry");
        entry.GetProperty("target").GetString().ShouldBe(jobId.ToString());
        entry.GetProperty("outcome").GetString().ShouldBe("retried");
        entry.GetProperty("role").GetString().ShouldBe("operate");
        entry.GetProperty("keyFingerprint").GetString().ShouldBe(Fingerprint(ApiFactory.AdminApiKey));
    }

    [Fact]
    public async Task Un_intento_que_no_prospera_tambien_queda_registrado()
    {
        await using var host = await StartAsync();
        var missing = Guid.NewGuid();

        (await host.Client.PostAsync($"/admin/jobs/{missing}/retry", content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await host.ScalarAsync<string>("SELECT outcome FROM admin_audit")).ShouldBe("not_found");
    }

    [Fact]
    public async Task Reintentar_varios_jobs_registra_una_entrada_por_cada_uno()
    {
        await using var host = await StartAsync();
        var first = await InsertFailedJobAsync(host, "7002");
        var second = await InsertFailedJobAsync(host, "7003");
        var missing = Guid.NewGuid();

        var response = await host.Client.PostAsJsonAsync("/admin/jobs/retry", new { ids = new[] { first, second, missing } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(3);
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit WHERE outcome = 'retried'")).ShouldBe(2);
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit WHERE outcome = 'not_found' AND target = '" + missing + "'")).ShouldBe(1);
    }

    [Fact]
    public async Task Disparar_la_sincronizacion_de_inventario_queda_registrado()
    {
        await using var host = await StartAsync();

        await host.Client.PostAsync("/admin/sync/inventory?full=true", content: null);

        (await host.ScalarAsync<string>("SELECT action FROM admin_audit")).ShouldBe("inventory.sync");
        (await host.ScalarAsync<string>("SELECT target FROM admin_audit")).ShouldBe("full");
    }

    [Fact]
    public async Task Una_accion_rechazada_por_403_o_401_no_se_registra_como_hecha()
    {
        await using var host = await StartAsync();
        using var reader = host.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadApiKey);
        using var anonymous = host.CreateAnonymousClient();

        (await reader.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await anonymous.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(0);
    }

    [Fact]
    public async Task La_clave_nunca_se_guarda_ni_en_la_tabla_ni_en_los_logs_solo_su_huella()
    {
        var sink = new CollectingSink();
        await using var host = await StartAsync(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        await host.Client.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null);

        var row = await host.ScalarAsync<string>("SELECT row_to_json(a)::text FROM admin_audit a");
        row.ShouldNotBeNull().ShouldNotContain(ApiFactory.AdminApiKey);
        row.ShouldContain(Fingerprint(ApiFactory.AdminApiKey));
        var logged = sink.Events.Where(logEvent => logEvent.Properties.ContainsKey("AuditAction")).ShouldHaveSingleItem();
        logged.Properties["KeyFingerprint"].ToString().ShouldBe($"\"{Fingerprint(ApiFactory.AdminApiKey)}\"");
        sink.Events.Select(logEvent => logEvent.RenderMessage()).ShouldNotContain(message => message.Contains(ApiFactory.AdminApiKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("UPDATE admin_audit SET outcome = 'otro'")]
    [InlineData("DELETE FROM admin_audit")]
    [InlineData("TRUNCATE admin_audit")]
    public async Task La_base_de_datos_rechaza_modificar_o_borrar_lo_registrado(string statement)
    {
        await using var host = await StartAsync();
        await host.Client.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null);

        var failure = await Should.ThrowAsync<PostgresException>(() => host.ExecuteAsync(statement));

        failure.MessageText.ShouldContain("solo inserción");
        (await host.ScalarAsync<string>("SELECT outcome FROM admin_audit")).ShouldBe("not_found");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task El_panel_no_ofrece_ninguna_forma_de_modificar_ni_borrar_la_auditoria(string method)
    {
        await using var host = await StartAsync();
        await host.Client.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null);
        var id = await host.ScalarAsync<Guid>("SELECT id FROM admin_audit");

        foreach (var path in new[] { "/admin/audit", $"/admin/audit/{id}" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            var response = await host.Client.SendAsync(request);
            response.IsSuccessStatusCode.ShouldBeFalse($"{method} {path} no debería existir");
        }

        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(1);
    }

    [Fact]
    public async Task El_listado_filtra_por_accion_y_por_huella_y_lo_puede_leer_una_clave_de_lectura()
    {
        await using var host = await StartAsync();
        await host.Client.PostAsync($"/admin/jobs/{Guid.NewGuid()}/retry", content: null);
        await host.Client.PostAsync("/admin/sync/inventory", content: null);
        using var reader = host.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadApiKey);

        using var all = await ReadAuditAsync(reader);
        using var byAction = await ReadAuditAsync(reader, "?action=inventory.sync");
        using var byOtherKey = await ReadAuditAsync(reader, "?keyFingerprint=00000000");
        using var byKey = await ReadAuditAsync(reader, $"?keyFingerprint={Fingerprint(ApiFactory.AdminApiKey).ToUpperInvariant()}");

        all.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(2);
        byAction.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("action").GetString().ShouldBe("inventory.sync");
        byOtherKey.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(0);
        byKey.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData("?from=ayer")]
    [InlineData("?to=nunca")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    public async Task Un_filtro_invalido_responde_400(string query)
    {
        await using var host = await StartAsync();

        (await host.Client.GetAsync($"/admin/audit{query}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
