using System.Net;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Infrastructure;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.IntegrationTests.Store;

/// <summary>El adaptador de WooCommerce contra una tienda simulada en memoria.</summary>
public sealed class WooCommerceGatewayTests : IDisposable
{
    private readonly FakeWooCommerceHandler _store = new();

    private ServiceProvider Provider(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Store:WooCommerce:BaseUrl"] = "https://tienda.example.com",
            ["Store:WooCommerce:ConsumerKey"] = FakeWooCommerceHandler.ConsumerKey,
            ["Store:WooCommerce:ConsumerSecret"] = FakeWooCommerceHandler.ConsumerSecret,
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStoreGateway(configuration);
        services.AddHttpClient(nameof(IStoreGateway)).ConfigurePrimaryHttpMessageHandler(() => _store);
        return services.BuildServiceProvider();
    }

    public void Dispose() => _store.Dispose();

    private static IStoreGateway Gateway(ServiceProvider provider) => provider.GetRequiredService<IStoreGateway>();

    [Fact]
    public async Task Busca_los_productos_por_SKU_y_omite_los_que_no_existen()
    {
        _store.AddProduct(11, "TAL-650");
        _store.AddProduct(12, "A00010");
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["TAL-650", "A00010", "NO-EXISTE"], CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value["TAL-650"].ShouldBe(new StoreProduct(11, "TAL-650"));
        result.Value["A00010"].Id.ShouldBe(12);
        result.Value.ContainsKey("NO-EXISTE").ShouldBeFalse();
        var request = _store.Requests.ShouldHaveSingleItem();
        request.PathAndQuery.ShouldStartWith("/wp-json/wc/v3/products?sku=TAL-650%2CA00010%2CNO-EXISTE&per_page=100");
    }

    [Fact]
    public async Task La_busqueda_no_distingue_mayusculas_pero_responde_con_el_SKU_pedido()
    {
        _store.AddProduct(11, "tal-650");
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["TAL-650"], CancellationToken.None);

        result.Value["TAL-650"].Id.ShouldBe(11);
    }

    [Fact]
    public async Task Busca_en_tandas_de_cien_SKU()
    {
        for (var n = 1; n <= 250; n++)
        {
            _store.AddProduct(n, $"S{n:D4}");
        }

        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync([.. Enumerable.Range(1, 250).Select(n => $"S{n:D4}")], CancellationToken.None);

        result.Value.Count.ShouldBe(250);
        _store.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Un_SKU_con_coma_no_se_busca_porque_la_lista_de_la_tienda_se_separa_por_comas()
    {
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A,B"], CancellationToken.None);

        result.Value.ShouldBeEmpty();
        _store.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Buscar_sin_SKU_no_llama_a_la_tienda()
    {
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync([], CancellationToken.None);

        result.Value.ShouldBeEmpty();
        _store.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Autentica_con_Basic_usando_la_clave_y_el_secreto()
    {
        await using var provider = Provider();

        await Gateway(provider).FindProductsBySkuAsync(["X"], CancellationToken.None);

        var header = _store.Authorizations.ShouldHaveSingleItem();
        header.Scheme.ShouldBe("Basic");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))
            .ShouldBe($"{FakeWooCommerceHandler.ConsumerKey}:{FakeWooCommerceHandler.ConsumerSecret}");
    }

    [Fact]
    public async Task Actualiza_stock_y_precio_en_un_lote_con_manage_stock()
    {
        var first = _store.AddProduct(11, "TAL-650", stock: 5, price: "100");
        var second = _store.AddProduct(12, "A00010");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync(
            [new StoreProductUpdate(11, "TAL-650", 40, 39990m), new StoreProductUpdate(12, "A00010", null, 2990.5m)],
            CancellationToken.None);

        result.Value.ShouldAllBe(outcome => outcome.Succeeded);
        result.Value.Select(outcome => outcome.Sku).ShouldBe(["TAL-650", "A00010"]);
        first.StockQuantity.ShouldBe(40);
        first.ManageStock.ShouldBeTrue();
        first.RegularPrice.ShouldBe("39990");
        second.StockQuantity.ShouldBe(0);
        second.ManageStock.ShouldBeFalse();
        second.RegularPrice.ShouldBe("2990.5");
        _store.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/wp-json/wc/v3/products/batch");
    }

    [Fact]
    public async Task Solo_envia_los_campos_que_cambian()
    {
        _store.AddProduct(11, "TAL-650");
        await using var provider = Provider();

        await Gateway(provider).UpdateProductsAsync([new StoreProductUpdate(11, "TAL-650", null, 500m)], CancellationToken.None);

        var item = _store.Batches.Single()["update"]!.AsArray().Single()!.AsObject();
        item.Select(property => property.Key).Order().ShouldBe(["id", "regular_price"]);
    }

    [Fact]
    public async Task Respeta_el_limite_de_cien_productos_por_lote()
    {
        for (var n = 1; n <= 250; n++)
        {
            _store.AddProduct(n, $"S{n:D4}");
        }

        await using var provider = Provider();
        var updates = Enumerable.Range(1, 250).Select(n => new StoreProductUpdate(n, $"S{n:D4}", n, null)).ToList();

        var result = await Gateway(provider).UpdateProductsAsync(updates, CancellationToken.None);

        result.Value.Count.ShouldBe(250);
        result.Value.ShouldAllBe(outcome => outcome.Succeeded);
        _store.Batches.Select(batch => batch["update"]!.AsArray().Count).ShouldBe([100, 100, 50]);
        _store.Products[250].StockQuantity.ShouldBe(250);
    }

    [Fact]
    public async Task Un_tamano_de_lote_menor_reparte_los_productos_en_mas_lotes()
    {
        for (var n = 1; n <= 5; n++)
        {
            _store.AddProduct(n, $"S{n}");
        }

        await using var provider = Provider(new() { ["Store:WooCommerce:BatchSize"] = "2" });

        await Gateway(provider).UpdateProductsAsync(
            [.. Enumerable.Range(1, 5).Select(n => new StoreProductUpdate(n, $"S{n}", n, null))],
            CancellationToken.None);

        _store.Batches.Select(batch => batch["update"]!.AsArray().Count).ShouldBe([2, 2, 1]);
    }

    [Fact]
    public async Task Un_producto_rechazado_no_impide_actualizar_a_los_demas()
    {
        var good = _store.AddProduct(11, "TAL-650");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync(
            [new StoreProductUpdate(99, "FANTASMA", 1, null), new StoreProductUpdate(11, "TAL-650", 7, null)],
            CancellationToken.None);

        var outcomes = result.Value;
        var failed = outcomes.Single(outcome => outcome.Sku == "FANTASMA");
        failed.Succeeded.ShouldBeFalse();
        failed.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        failed.Error.Code.ShouldBe(StoreErrors.Rejected);
        failed.Error.Message.ShouldContain("woocommerce_rest_product_invalid_id");
        outcomes.Single(outcome => outcome.Sku == "TAL-650").Succeeded.ShouldBeTrue();
        good.StockQuantity.ShouldBe(7);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Las_credenciales_rechazadas_fallan_toda_la_operacion_como_definitivas(HttpStatusCode status)
    {
        _store.RespondNextWith(status, """{"code":"woocommerce_rest_cannot_create","message":"No puedes crear.","data":{"status":401}}""");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync([new StoreProductUpdate(1, "A", 1, null)], CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(StoreErrors.Unauthorized);
        result.Error.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Message.ShouldContain("No puedes crear");
    }

    [Fact]
    public async Task Credenciales_incorrectas_en_la_busqueda_son_un_error_de_autorizacion()
    {
        await using var provider = Provider(new()
        {
            ["Store:WooCommerce:ConsumerSecret"] = "cs_ffffffffffffffffffffffffffffffffffffffff",
        });

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(StoreErrors.Unauthorized);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Transient, StoreErrors.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, ErrorKind.Transient, StoreErrors.ServerError)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Transient, StoreErrors.ServerError)]
    [InlineData(HttpStatusCode.RequestTimeout, ErrorKind.Transient, StoreErrors.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Rejected, StoreErrors.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ErrorKind.Rejected, StoreErrors.Rejected)]
    public async Task Una_falla_del_lote_se_clasifica_y_se_asigna_a_sus_productos(HttpStatusCode status, ErrorKind kind, string code)
    {
        _store.RespondNextWith(status, "<html>error</html>");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync(
            [new StoreProductUpdate(1, "A", 1, null), new StoreProductUpdate(2, "B", 1, null)],
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldAllBe(outcome => !outcome.Succeeded && outcome.Error!.Kind == kind && outcome.Error.Code == code);
    }

    [Fact]
    public async Task Tras_una_falla_pasajera_no_insiste_con_los_lotes_siguientes()
    {
        for (var n = 1; n <= 5; n++)
        {
            _store.AddProduct(n, $"S{n}");
        }

        _store.RespondNextWith(HttpStatusCode.ServiceUnavailable);
        await using var provider = Provider(new() { ["Store:WooCommerce:BatchSize"] = "2" });

        var result = await Gateway(provider).UpdateProductsAsync(
            [.. Enumerable.Range(1, 5).Select(n => new StoreProductUpdate(n, $"S{n}", n, null))],
            CancellationToken.None);

        result.Value.Count.ShouldBe(5);
        result.Value.ShouldAllBe(outcome => outcome.Error!.IsTransient);
        _store.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Una_falla_definitiva_de_un_lote_no_detiene_los_siguientes()
    {
        for (var n = 1; n <= 4; n++)
        {
            _store.AddProduct(n, $"S{n}");
        }

        _store.RespondNextWith(HttpStatusCode.BadRequest);
        await using var provider = Provider(new() { ["Store:WooCommerce:BatchSize"] = "2" });

        var result = await Gateway(provider).UpdateProductsAsync(
            [.. Enumerable.Range(1, 4).Select(n => new StoreProductUpdate(n, $"S{n}", n, null))],
            CancellationToken.None);

        result.Value.Select(outcome => outcome.Succeeded).ShouldBe([false, false, true, true]);
    }

    [Fact]
    public async Task Sin_conexion_es_una_falla_pasajera()
    {
        _store.FailNextWith(new HttpRequestException("Connection refused"));
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.Error!.Code.ShouldBe(StoreErrors.Unreachable);
        result.Error.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task Una_conexion_cortada_a_mitad_de_la_respuesta_es_una_falla_pasajera()
    {
        _store.FailNextWith(new IOException("Unexpected end of stream"));
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.Error!.Code.ShouldBe(StoreErrors.Unreachable);
        result.Error.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task El_tiempo_agotado_es_una_falla_pasajera()
    {
        _store.FailNextWith(new TaskCanceledException("timeout"));
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.Error!.Code.ShouldBe(StoreErrors.Timeout);
        result.Error.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task La_cancelacion_pedida_se_propaga()
    {
        await using var provider = Provider();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => Gateway(provider).FindProductsBySkuAsync(["A"], cts.Token));
    }

    [Fact]
    public async Task Una_respuesta_que_no_es_JSON_es_una_falla_pasajera()
    {
        _store.RespondNextWith(HttpStatusCode.OK, "<html>mantenimiento</html>");
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.Error!.Code.ShouldBe(StoreErrors.InvalidResponse);
        result.Error.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task Si_la_busqueda_no_devuelve_una_lista_es_una_respuesta_invalida()
    {
        _store.RespondNextWith(HttpStatusCode.OK, "{}");
        await using var provider = Provider();

        var result = await Gateway(provider).FindProductsBySkuAsync(["A"], CancellationToken.None);

        result.Error!.Code.ShouldBe(StoreErrors.InvalidResponse);
    }

    [Fact]
    public async Task Un_producto_ausente_de_la_respuesta_del_lote_se_informa_como_no_confirmado()
    {
        _store.RespondNextWith(HttpStatusCode.OK, """{"update":[]}""");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync([new StoreProductUpdate(1, "A", 1, null)], CancellationToken.None);

        var outcome = result.Value.ShouldHaveSingleItem();
        outcome.Succeeded.ShouldBeFalse();
        outcome.Error!.Code.ShouldBe(StoreErrors.InvalidResponse);
    }

    [Fact]
    public async Task Un_error_5xx_de_un_producto_es_pasajero()
    {
        _store.RespondNextWith(
            HttpStatusCode.OK,
            """{"update":[{"id":1,"error":{"code":"internal","message":"Falló la base de datos.","data":{"status":500}}}]}""");
        await using var provider = Provider();

        var result = await Gateway(provider).UpdateProductsAsync([new StoreProductUpdate(1, "A", 1, null)], CancellationToken.None);

        result.Value.Single().Error!.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task Sin_configuracion_la_tienda_se_informa_como_no_configurada()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStoreGateway(configuration);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<Infrastructure.Store.WooCommerceOptions>>().Value.IsConfigured.ShouldBeFalse();
        Gateway(provider).IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task Con_configuracion_la_tienda_se_informa_como_configurada()
    {
        await using var provider = Provider();

        Gateway(provider).IsConfigured.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Store:WooCommerce:BaseUrl", "no-es-una-url", "BaseUrl")]
    [InlineData("Store:WooCommerce:BaseUrl", "ftp://tienda.example.com", "BaseUrl")]
    [InlineData("Store:WooCommerce:ConsumerKey", "ck_corta", "ConsumerKey")]
    [InlineData("Store:WooCommerce:ConsumerKey", "cs_0123456789abcdef0123456789abcdef01234567", "ConsumerKey")]
    [InlineData("Store:WooCommerce:ConsumerKey", "ck_0123456789ABCDEF0123456789abcdef01234567", "ConsumerKey")]
    [InlineData("Store:WooCommerce:ConsumerSecret", "", "ConsumerSecret")]
    [InlineData("Store:WooCommerce:BatchSize", "0", "BatchSize")]
    [InlineData("Store:WooCommerce:BatchSize", "101", "BatchSize")]
    [InlineData("Store:WooCommerce:RequestTimeout", "00:00:00", "RequestTimeout")]
    public async Task Las_opciones_invalidas_se_rechazan_con_un_mensaje_que_nombra_la_variable(string key, string value, string expected)
    {
        await using var provider = Provider(new() { [key] = value });

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<Infrastructure.Store.WooCommerceOptions>>().Value);

        failure.Message.ShouldContain(expected);
        failure.Message.ShouldContain("Store__WooCommerce__", Case.Insensitive);
    }

    [Fact]
    public async Task Definir_solo_parte_de_la_configuracion_se_rechaza()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:WooCommerce:BaseUrl"] = "https://tienda.example.com" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStoreGateway(configuration);
        await using var provider = services.BuildServiceProvider();

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<Infrastructure.Store.WooCommerceOptions>>().Value);

        failure.Failures.Count().ShouldBe(2); // falta la clave y el secreto
    }
}
