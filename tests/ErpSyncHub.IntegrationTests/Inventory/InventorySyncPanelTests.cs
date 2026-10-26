using System.Net;
using System.Text.Json;
using ErpSyncHub.Application.Inventory;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace ErpSyncHub.IntegrationTests.Inventory;

/// <summary>Los endpoints del panel: disparar la sincronización a mano y consultar sus ejecuciones.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class InventorySyncPanelTests(PostgresFixture postgres) : IDisposable
{
    private static readonly Dictionary<string, string> PeriodicOff = new() { ["Sync:Inventory:Enabled"] = "false" };

    private readonly FakeWooCommerceHandler _store = new();
    private readonly SapMockHost _sap = new();

    public void Dispose()
    {
        _sap.Dispose();
        _store.Dispose();
    }

    private Task<WebhookHost> StartAsync(bool withStore = true) =>
        WebhookHost.StartAsync(
            postgres,
            InventorySyncHost.Configure(withStore ? _store : null, settings: PeriodicOff),
            sap: _sap);

    private static Task<HttpResponseMessage> Trigger(WebhookHost host, string query = "") =>
        host.Client.PostAsync($"/admin/sync/inventory{query}", content: null);

    [Fact]
    public async Task Disparar_la_sincronizacion_responde_con_el_registro_de_la_ejecucion()
    {
        var drill = _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();

        var response = await Trigger(host);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        var run = body.RootElement;
        run.GetProperty("status").GetString().ShouldBe("succeeded");
        run.GetProperty("trigger").GetString().ShouldBe("manual");
        run.GetProperty("full").GetBoolean().ShouldBeFalse();
        run.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        run.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.String);
        run.GetProperty("durationSeconds").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        run.GetProperty("items").GetProperty("read").GetInt32().ShouldBe(24);
        run.GetProperty("items").GetProperty("updated").GetInt32().ShouldBe(1);
        run.GetProperty("items").GetProperty("skipped").GetInt32().ShouldBe(23);
        run.GetProperty("items").GetProperty("unchanged").GetInt32().ShouldBe(0);
        run.GetProperty("items").GetProperty("failed").GetInt32().ShouldBe(0);
        run.TryGetProperty("error", out var error).ShouldBeTrue();
        error.ValueKind.ShouldBe(JsonValueKind.Null);
        drill.StockQuantity.ShouldBe(45);
    }

    [Fact]
    public async Task La_sincronizacion_manual_funciona_aunque_la_periodica_este_desactivada()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();

        (await Trigger(host)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(1);
    }

    [Fact]
    public async Task Con_full_envia_todo_aunque_nada_haya_cambiado_y_lee_todo_el_maestro()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();
        await Trigger(host);

        var normal = await Trigger(host);
        var full = await Trigger(host, "?full=true");

        using var normalBody = await WebhookHost.ReadJsonAsync(normal);
        normalBody.RootElement.GetProperty("items").GetProperty("unchanged").GetInt32().ShouldBe(1);
        normalBody.RootElement.GetProperty("items").GetProperty("updated").GetInt32().ShouldBe(0);
        using var fullBody = await WebhookHost.ReadJsonAsync(full);
        fullBody.RootElement.GetProperty("full").GetBoolean().ShouldBeTrue();
        fullBody.RootElement.GetProperty("changedSince").ValueKind.ShouldBe(JsonValueKind.Null);
        fullBody.RootElement.GetProperty("items").GetProperty("updated").GetInt32().ShouldBe(1);
        _store.Batches.Count().ShouldBe(2); // la primera pasada y la completa; la normal no envió nada
    }

    [Fact]
    public async Task Si_ya_hay_una_sincronizacion_en_curso_responde_409_con_ProblemDetails()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();
        await using var held = await host.Services.GetRequiredService<IInventorySyncLock>().TryAcquireAsync(CancellationToken.None);
        held.ShouldNotBeNull();

        var response = await Trigger(host);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("title").GetString().ShouldBe("Conflicto");
        body.RootElement.GetProperty("detail").GetString().ShouldBe("Ya hay una sincronización de stock y precios en curso.");
        body.RootElement.GetProperty("code").GetString().ShouldBe("inventory_sync.already_running");
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(0);
        _store.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dos_disparos_simultaneos_ejecutan_una_sola_sincronizacion()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Trigger(host)));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);
        var conflicts = responses.Count(response => response.StatusCode == HttpStatusCode.Conflict);
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs")).ShouldBe(4 - conflicts);
        (await host.ScalarAsync<long>("SELECT count(*) FROM inventory_sync_runs WHERE status = 'running'")).ShouldBe(0);
    }

    [Fact]
    public async Task Sin_la_tienda_configurada_responde_409_y_dice_que_falta()
    {
        await using var host = await StartAsync(withStore: false);

        var response = await Trigger(host);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("code").GetString().ShouldBe("inventory_sync.store_not_configured");
        body.RootElement.GetProperty("detail").GetString()!.ShouldContain("Store__WooCommerce__BaseUrl");
    }

    [Fact]
    public async Task Una_ejecucion_fallida_responde_200_con_el_estado_y_el_error()
    {
        _store.AddProduct(11, "A00001");
        _store.RespondNextWith(HttpStatusCode.Unauthorized, """{"code":"woocommerce_rest_cannot_view","message":"Sin permiso.","data":{"status":401}}""");
        await using var host = await StartAsync();

        var response = await Trigger(host);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("status").GetString().ShouldBe("failed");
        body.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("store.unauthorized");
        body.RootElement.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("Sin permiso");
    }

    [Fact]
    public async Task Consultar_la_ultima_sin_ejecuciones_responde_404()
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync("/admin/sync/inventory/last");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Consultar_la_ultima_devuelve_la_ejecucion_mas_reciente()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();
        await Trigger(host);
        var second = await Trigger(host);
        using var secondBody = await WebhookHost.ReadJsonAsync(second);

        var response = await host.Client.GetAsync("/admin/sync/inventory/last");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var last = await WebhookHost.ReadJsonAsync(response);
        last.RootElement.GetProperty("id").GetGuid().ShouldBe(secondBody.RootElement.GetProperty("id").GetGuid());
        last.RootElement.GetProperty("items").GetProperty("unchanged").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Listar_las_ejecuciones_las_devuelve_de_la_mas_reciente_a_la_mas_antigua_con_limite()
    {
        _store.AddProduct(11, "A00001");
        await using var host = await StartAsync();
        await Trigger(host);
        await Trigger(host);
        await Trigger(host);

        var all = await host.Client.GetAsync("/admin/sync/inventory/runs");
        var two = await host.Client.GetAsync("/admin/sync/inventory/runs?limit=2");

        using var allBody = await WebhookHost.ReadJsonAsync(all);
        using var twoBody = await WebhookHost.ReadJsonAsync(two);
        var started = allBody.RootElement.EnumerateArray().Select(run => run.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        started.Count.ShouldBe(3);
        started.ShouldBe([.. started.OrderByDescending(date => date)]);
        twoBody.RootElement.GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    public async Task Un_limite_fuera_de_rango_responde_400(string limit)
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync($"/admin/sync/inventory/runs?limit={limit}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }
}
