using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>El panel (<c>/admin/*</c>) solo responde a quien presenta una API key válida.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class AdminAuthenticationTests(PostgresFixture postgres)
{
    private const string Panel = "/admin/sync/inventory/runs";
    private const string KeyA = "primera-clave-del-panel-0123456789abcdef";
    private const string KeyB = "segunda-clave-del-panel-fedcba9876543210";

    private static HttpRequestMessage Get(string path, params string[] keys)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (keys.Length > 0)
        {
            request.Headers.Add("X-Api-Key", keys);
        }

        return request;
    }

    private Task<WebhookHost> StartAsync(
        string? adminApiKey = ApiFactory.AdminApiKey,
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null,
        string? readApiKeys = null) =>
        WebhookHost.StartAsync(postgres, builder =>
        {
            builder.UseSetting("Admin:OperateApiKeys", adminApiKey ?? string.Empty);
            builder.UseSetting("Admin:ReadApiKeys", readApiKeys ?? string.Empty);
            configure?.Invoke(builder);
        });

    [Fact]
    public async Task Sin_clave_el_panel_responde_401_con_ProblemDetails_y_pide_la_cabecera()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var response = await client.GetAsync(Panel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.WwwAuthenticate.ToString().ShouldContain("X-Api-Key");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("No autorizado");
        body.ShouldContain("X-Api-Key");
    }

    [Fact]
    public async Task Con_la_clave_correcta_el_panel_responde()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var response = await client.SendAsync(Get(Panel, ApiFactory.AdminApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("clave-incorrecta-pero-de-largo-suficiente-0123")]
    [InlineData("CLAVE-DE-ADMINISTRACION-DE-PRUEBAS-0123456789")]
    [InlineData("clave-de-administracion-de-pruebas-012345678")]
    [InlineData("clave-de-administracion-de-pruebas-01234567890")]
    [InlineData("x")]
    [InlineData(" clave-de-administracion-de-pruebas-0123456789")]
    public async Task Una_clave_distinta_incluso_en_un_caracter_o_en_mayusculas_se_rechaza(string key)
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var response = await client.SendAsync(Get(Panel, key));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Una_clave_vacia_se_rechaza()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Panel);
        request.Headers.TryAddWithoutValidation("X-Api-Key", string.Empty);

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Una_cabecera_repetida_es_ambigua_y_se_rechaza_aunque_una_de_las_claves_sea_valida()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var response = await client.SendAsync(Get(Panel, ApiFactory.AdminApiKey, ApiFactory.AdminApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task La_clave_en_la_consulta_o_en_Authorization_no_sirve()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();
        using var withAuthorization = new HttpRequestMessage(HttpMethod.Get, Panel);
        withAuthorization.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.AdminApiKey);

        (await client.GetAsync($"{Panel}?X-Api-Key={ApiFactory.AdminApiKey}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(withAuthorization)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/admin/ruta-que-no-existe")]
    [InlineData("/ADMIN/sync/inventory/last")]
    [InlineData("/admin")]
    public async Task Cualquier_ruta_bajo_admin_exige_la_clave_aunque_no_exista(string path)
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        (await client.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Con_clave_una_ruta_inexistente_bajo_admin_responde_404()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get("/admin/ruta-que-no-existe", ApiFactory.AdminApiKey))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disparar_la_sincronizacion_sin_clave_se_rechaza_y_no_ejecuta_nada()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var response = await client.PostAsync("/admin/sync/inventory", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(0);
    }

    [Fact]
    public async Task Varias_claves_separadas_por_coma_se_aceptan_todas_para_rotarlas_sin_cortar_el_acceso()
    {
        await using var host = await StartAsync($"{KeyA},{KeyB}");
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, KeyA))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get(Panel, KeyB))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get(Panel, "tercera-clave-que-no-esta-configurada-0123"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Los_espacios_alrededor_de_cada_clave_y_las_comas_sobrantes_se_ignoran()
    {
        await using var host = await StartAsync($"  {KeyA} , ,{KeyB},");
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, KeyA))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get(Panel, KeyB))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Al_retirar_la_clave_vieja_deja_de_valer_y_la_nueva_sigue_funcionando()
    {
        // Paso 3 de la rotación: el hub se reinicia solo con la clave nueva.
        await using var host = await StartAsync(KeyB);
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, KeyA))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get(Panel, KeyB))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sin_ninguna_clave_configurada_el_panel_responde_503_y_no_queda_abierto()
    {
        await using var host = await StartAsync(adminApiKey: null);
        using var client = host.CreateAnonymousClient();

        var withoutKey = await client.GetAsync(Panel);
        var withAnyKey = await client.SendAsync(Get(Panel, ApiFactory.AdminApiKey));

        withoutKey.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        withAnyKey.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await withoutKey.Content.ReadAsStringAsync()).ShouldContain("Admin__OperateApiKeys");
        withoutKey.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Sin_clave_configurada_el_resto_del_hub_funciona_y_se_avisa_en_el_log_al_arrancar()
    {
        var sink = new CollectingSink();
        await using var host = await StartAsync(
            adminApiKey: null,
            configure: builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        using var client = host.CreateAnonymousClient();

        (await client.GetAsync("/health")).IsSuccessStatusCode.ShouldBeTrue();
        sink.Events.ShouldContain(logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.RenderMessage().Contains("DESHABILITADO", StringComparison.Ordinal)
            && logEvent.RenderMessage().Contains("Admin__OperateApiKeys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task El_webhook_la_salud_y_la_documentacion_no_piden_la_clave_del_panel()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        (await client.GetAsync("/health")).IsSuccessStatusCode.ShouldBeTrue();
        var webhook = await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"));
        webhook.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task El_webhook_con_firma_incorrecta_sigue_respondiendo_401_por_su_firma_y_no_por_la_clave()
    {
        await using var host = await StartAsync();

        var response = await host.PostAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"), signature: "firma-incorrecta");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldNotContain("X-Api-Key");
    }

    [Fact]
    public async Task Las_claves_nunca_aparecen_en_los_logs_ni_las_presentadas_ni_las_configuradas()
    {
        var sink = new CollectingSink();
        const string wrongKey = "clave-incorrecta-que-alguien-intento-0123456";
        await using var host = await StartAsync(
            $"{KeyA},{KeyB}",
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        using var client = host.CreateAnonymousClient();

        await client.SendAsync(Get(Panel, wrongKey));
        await client.SendAsync(Get(Panel));
        await client.SendAsync(Get(Panel, KeyA));
        await client.SendAsync(Get($"{Panel}?limit=5", KeyB));

        var rendered = sink.Events.Select(Render).ToList();
        rendered.ShouldNotBeEmpty();
        rendered.ShouldNotContain(text => text.Contains(wrongKey, StringComparison.Ordinal));
        rendered.ShouldNotContain(text => text.Contains(KeyA, StringComparison.Ordinal));
        rendered.ShouldNotContain(text => text.Contains(KeyB, StringComparison.Ordinal));
        var keyStart = wrongKey.Substring(0, 12);
        rendered.ShouldNotContain(text => text.Contains(keyStart, StringComparison.Ordinal));
        sink.Events.Count(logEvent => logEvent.Level == LogEventLevel.Warning && logEvent.RenderMessage().Contains("Acceso rechazado", StringComparison.Ordinal))
            .ShouldBe(2);
    }

    [Theory]
    [InlineData("corta", "Admin:OperateApiKeys")]
    [InlineData("0123456789012345678901234567890", "32")] // 31 caracteres
    [InlineData("clave con espacios en el medio de la clave 0123456789", "espacios")]
    [InlineData("primera-clave-del-panel-0123456789abcdef,corta", "n.º 2")]
    [InlineData("primera-clave-del-panel-0123456789abcdef,primera-clave-del-panel-0123456789abcdef", "repetida")]
    public async Task Una_clave_invalida_impide_arrancar_y_nombra_la_variable(string value, string expected)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, adminApiKey: value);

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain(expected);
        failure.Message.ShouldContain("Admin__OperateApiKeys");
    }

    private static HttpRequestMessage Post(string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    [Fact]
    public async Task Una_clave_de_lectura_consulta_pero_no_ejecuta_acciones_y_responde_403()
    {
        await using var host = await StartAsync(readApiKeys: ApiFactory.ReadApiKey);
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, ApiFactory.ReadApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get("/admin/jobs", ApiFactory.ReadApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get("/admin/metrics", ApiFactory.ReadApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var trigger = await client.SendAsync(Post("/admin/sync/inventory", ApiFactory.ReadApiKey));
        var retry = await client.SendAsync(Post($"/admin/jobs/{Guid.NewGuid()}/retry", ApiFactory.ReadApiKey));
        using var retryMany = new HttpRequestMessage(HttpMethod.Post, "/admin/jobs/retry") { Content = JsonContent.Create(new { ids = new[] { Guid.NewGuid() } }) };
        retryMany.Headers.Add("X-Api-Key", ApiFactory.ReadApiKey);
        var many = await client.SendAsync(retryMany);

        trigger.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        retry.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        many.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        trigger.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await trigger.Content.ReadAsStringAsync()).ShouldContain("clave de operación");
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(0); // la acción no se ejecutó
    }

    [Fact]
    public async Task Una_clave_de_operacion_tambien_lee_y_ejecuta_acciones()
    {
        await using var host = await StartAsync(readApiKeys: ApiFactory.ReadApiKey);
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, ApiFactory.AdminApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Post($"/admin/jobs/{Guid.NewGuid()}/retry", ApiFactory.AdminApiKey))).StatusCode.ShouldBe(HttpStatusCode.NotFound); // llegó al endpoint
    }

    [Fact]
    public async Task Solo_con_claves_de_lectura_el_panel_esta_habilitado_y_las_acciones_responden_403()
    {
        await using var host = await StartAsync(adminApiKey: null, readApiKeys: ApiFactory.ReadApiKey);
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, ApiFactory.ReadApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Post("/admin/sync/inventory", ApiFactory.ReadApiKey))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Las_claves_de_lectura_tambien_se_rotan_con_varias_separadas_por_coma()
    {
        await using var host = await StartAsync(readApiKeys: $"{KeyA},{KeyB}");
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, KeyA))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Get(Panel, KeyB))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(Post("/admin/sync/inventory", KeyB))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Una_misma_clave_en_las_dos_listas_impide_arrancar()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, adminApiKey: KeyA, readApiKey: KeyA);

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("Admin__ReadApiKeys");
        failure.Message.ShouldContain("Admin__OperateApiKeys");
        failure.Message.ShouldContain("un solo nivel");
    }

    [Fact]
    public async Task Una_clave_de_lectura_invalida_nombra_su_propia_variable()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, readApiKey: "corta");

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("Admin:ReadApiKeys");
        failure.Message.ShouldContain("ERPSYNC_ADMIN_READ_API_KEYS");
    }

    [Fact]
    public async Task El_rechazo_a_una_clave_de_lectura_se_registra_con_su_huella_y_nunca_con_la_clave()
    {
        var sink = new CollectingSink();
        await using var host = await StartAsync(
            readApiKeys: ApiFactory.ReadApiKey,
            configure: builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        using var client = host.CreateAnonymousClient();

        await client.SendAsync(Post("/admin/sync/inventory", ApiFactory.ReadApiKey));

        var forbidden = sink.Events.Single(logEvent => logEvent.RenderMessage().Contains("clave de lectura", StringComparison.Ordinal));
        forbidden.Level.ShouldBe(LogEventLevel.Warning);
        forbidden.Properties["KeyFingerprint"].ToString().Trim('"').Length.ShouldBe(8);
        sink.Events.Select(Render).ShouldNotContain(text => text.Contains(ApiFactory.ReadApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Una_clave_de_exactamente_32_caracteres_se_acepta()
    {
        var key = new string('k', 32);
        await using var host = await StartAsync(key);
        using var client = host.CreateAnonymousClient();

        (await client.SendAsync(Get(Panel, key))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string Render(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        logEvent.RenderMessage(writer);
        foreach (var property in logEvent.Properties)
        {
            writer.Write(' ');
            writer.Write(property.Key);
            writer.Write('=');
            property.Value.Render(writer);
        }

        if (logEvent.Exception is not null)
        {
            writer.Write(logEvent.Exception.ToString());
        }

        return writer.ToString();
    }
}
