using System.Net;
using System.Text.Json;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>El panel lista, filtra y elimina mapeos; los datos personales de los clientes salen ocultos salvo para quien opera.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class MappingPanelTests(PostgresFixture postgres)
{
    private async Task<WebhookHost> StartAsync()
    {
        var host = await WebhookHost.StartAsync(postgres, builder => builder.UseSetting("Admin:ReadApiKeys", ApiFactory.ReadApiKey));
        await host.ExecuteAsync(
            """
            INSERT INTO product_mappings (id, sku, item_code, created_at) VALUES
              (gen_random_uuid(), 'TAL-650', 'A00001', now()),
              (gen_random_uuid(), 'TAL-700', 'A00002', now()),
              (gen_random_uuid(), 'HUI-5M', 'A00010', now()),
              (gen_random_uuid(), '50%_OFF', 'A00099', now());
            INSERT INTO customer_mappings (id, external_key, card_code, created_at) VALUES
              (gen_random_uuid(), 'customer:4521', 'C18765432-7', now() - interval '2 minutes'),
              (gen_random_uuid(), 'email:valentina@example.com', 'WC-G-1A2B3C', now() - interval '1 minute'),
              (gen_random_uuid(), 'customer:77', 'WC77', now());
            """);
        return host;
    }

    private static HttpClient Reader(WebhookHost host)
    {
        var client = host.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ReadApiKey);
        return client;
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string[] Values(JsonDocument document, string property) =>
        [.. document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(property).GetString()!)];

    // --- Productos ---

    [Fact]
    public async Task Lista_los_mapeos_de_producto_ordenados_por_SKU_y_los_puede_leer_una_clave_de_lectura()
    {
        await using var host = await StartAsync();
        using var reader = Reader(host);

        using var page = await ReadAsync(reader, "/admin/mappings/products");

        Values(page, "sku").ShouldBe(["50%_OFF", "HUI-5M", "TAL-650", "TAL-700"]);
        page.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Filtra_por_SKU_y_por_ItemCode_sin_distinguir_mayusculas_y_trata_los_comodines_como_texto()
    {
        await using var host = await StartAsync();

        using var bySku = await ReadAsync(host.Client, "/admin/mappings/products?sku=tal-");
        using var byItem = await ReadAsync(host.Client, "/admin/mappings/products?itemCode=0001");
        using var byItemLower = await ReadAsync(host.Client, "/admin/mappings/products?itemCode=a00010");
        using var wildcard = await ReadAsync(host.Client, "/admin/mappings/products?sku=_AL");
        using var literal = await ReadAsync(host.Client, "/admin/mappings/products?sku=0%25_O");

        Values(bySku, "sku").ShouldBe(["TAL-650", "TAL-700"]);
        Values(byItem, "sku").ShouldBe(["HUI-5M", "TAL-650"]);
        Values(byItemLower, "sku").ShouldBe(["HUI-5M"]);
        Values(wildcard, "sku").ShouldBeEmpty(); // "_" no es "un carácter cualquiera": "_AL" no coincide con "TAL"
        Values(literal, "sku").ShouldBe(["50%_OFF"]);
    }

    [Fact]
    public async Task Pagina_los_mapeos_de_producto()
    {
        await using var host = await StartAsync();

        using var first = await ReadAsync(host.Client, "/admin/mappings/products?pageSize=3");
        using var second = await ReadAsync(host.Client, "/admin/mappings/products?pageSize=3&page=2");

        Values(first, "sku").Length.ShouldBe(3);
        Values(second, "sku").ShouldBe(["TAL-700"]);
        first.RootElement.GetProperty("totalPages").GetInt32().ShouldBe(2);
    }

    // --- Clientes ---

    [Fact]
    public async Task Una_clave_de_lectura_ve_los_mapeos_de_cliente_con_los_datos_personales_ocultos()
    {
        await using var host = await StartAsync();
        using var reader = Reader(host);

        using var page = await ReadAsync(reader, "/admin/mappings/customers");

        page.RootElement.GetProperty("personalDataHidden").GetBoolean().ShouldBeTrue();
        var rendered = page.RootElement.GetRawText();
        rendered.ShouldNotContain("valentina@example.com");
        rendered.ShouldNotContain("18765432");
        Values(page, "key").ShouldBe(["customer:77", "email:[correo]", "customer:4521"]);
        Values(page, "cardCode").ShouldBe(["WC77", "WC-G-1A2B3C", "C[RUT]"]);
    }

    [Fact]
    public async Task Una_clave_de_operacion_puede_pedir_los_datos_completos()
    {
        await using var host = await StartAsync();

        using var page = await ReadAsync(host.Client, "/admin/mappings/customers?includePersonalData=true");

        page.RootElement.GetProperty("personalDataHidden").GetBoolean().ShouldBeFalse();
        Values(page, "key").ShouldBe(["customer:77", "email:valentina@example.com", "customer:4521"]);
        Values(page, "cardCode").ShouldContain("C18765432-7");
    }

    [Theory]
    [InlineData("?includePersonalData=true")]
    [InlineData("?key=customer:4521")]
    [InlineData("?cardCode=C18765432-7")]
    public async Task Una_clave_de_lectura_no_puede_ver_ni_buscar_por_datos_personales(string query)
    {
        await using var host = await StartAsync();
        using var reader = Reader(host);

        var response = await reader.GetAsync($"/admin/mappings/customers{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Filtra_los_clientes_por_tipo_y_por_clave_o_codigo_exactos()
    {
        await using var host = await StartAsync();

        using var guests = await ReadAsync(host.Client, "/admin/mappings/customers?kind=guest");
        using var registered = await ReadAsync(host.Client, "/admin/mappings/customers?kind=registered");
        using var byKey = await ReadAsync(host.Client, "/admin/mappings/customers?key=customer:4521&includePersonalData=true");
        using var byCode = await ReadAsync(host.Client, "/admin/mappings/customers?cardCode=C18765432-7&includePersonalData=true");

        guests.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(1);
        registered.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(2);
        Values(byKey, "cardCode").ShouldBe(["C18765432-7"]);
        Values(byCode, "key").ShouldBe(["customer:4521"]);
    }

    [Theory]
    [InlineData("/admin/mappings/customers?kind=otro")]
    [InlineData("/admin/mappings/customers?page=0")]
    [InlineData("/admin/mappings/products?pageSize=101")]
    [InlineData("/admin/mappings/products?page=-1")]
    public async Task Un_filtro_invalido_responde_400(string url)
    {
        await using var host = await StartAsync();

        (await host.Client.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sin_clave_los_mapeos_responden_401()
    {
        await using var host = await StartAsync();
        using var anonymous = host.CreateAnonymousClient();

        (await anonymous.GetAsync("/admin/mappings/products")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync("/admin/mappings/products?sku=TAL-650")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- Eliminar ---

    [Fact]
    public async Task Eliminar_un_mapeo_de_producto_responde_204_lo_borra_y_queda_en_la_auditoria()
    {
        await using var host = await StartAsync();

        var response = await host.Client.DeleteAsync("/admin/mappings/products?sku=TAL-650");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.ScalarAsync<long>("SELECT count(*) FROM product_mappings WHERE sku = 'TAL-650'")).ShouldBe(0);
        (await host.ScalarAsync<long>("SELECT count(*) FROM product_mappings")).ShouldBe(3);
        (await host.ScalarAsync<string>("SELECT action || '|' || target || '|' || outcome FROM admin_audit")).ShouldBe("mapping.delete_product|TAL-650|deleted");
    }

    [Fact]
    public async Task Eliminar_un_mapeo_que_no_existe_responde_404_y_tambien_se_audita()
    {
        await using var host = await StartAsync();

        var response = await host.Client.DeleteAsync("/admin/mappings/products?sku=NO-EXISTE");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.ScalarAsync<string>("SELECT outcome FROM admin_audit")).ShouldBe("not_found");
    }

    [Fact]
    public async Task Eliminar_el_mapeo_de_un_invitado_no_deja_su_correo_en_la_auditoria()
    {
        await using var host = await StartAsync();

        var response = await host.Client.DeleteAsync("/admin/mappings/customers?key=email:valentina@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.ScalarAsync<long>("SELECT count(*) FROM customer_mappings")).ShouldBe(2);
        var audited = await host.ScalarAsync<string>("SELECT row_to_json(a)::text FROM admin_audit a");
        audited.ShouldNotBeNull().ShouldContain("email:[correo]");
        audited.ShouldNotContain("valentina");
    }

    [Fact]
    public async Task Una_clave_de_lectura_no_puede_eliminar_mapeos_y_no_se_borra_nada()
    {
        await using var host = await StartAsync();
        using var reader = Reader(host);

        (await reader.DeleteAsync("/admin/mappings/products?sku=TAL-650")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync("/admin/mappings/customers?key=customer:4521")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await host.ScalarAsync<long>("SELECT count(*) FROM product_mappings")).ShouldBe(4);
        (await host.ScalarAsync<long>("SELECT count(*) FROM customer_mappings")).ShouldBe(3);
        (await host.ScalarAsync<long>("SELECT count(*) FROM admin_audit")).ShouldBe(0);
    }

    [Theory]
    [InlineData("/admin/mappings/products")]
    [InlineData("/admin/mappings/customers")]
    [InlineData("/admin/mappings/products?sku=")]
    public async Task Eliminar_sin_indicar_cual_responde_400(string url)
    {
        await using var host = await StartAsync();

        (await host.Client.DeleteAsync(url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
