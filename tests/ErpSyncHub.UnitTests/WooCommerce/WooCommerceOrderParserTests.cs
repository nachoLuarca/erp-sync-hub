using System.Text;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.UnitTests.WooCommerce;

public class WooCommerceOrderParserTests
{
    [Fact]
    public void Lee_un_pedido_real_de_un_cliente_registrado()
    {
        var result = WooCommerceOrderParser.Parse(Fixtures.ReadBytes("woocommerce/order-processing.json"));

        result.IsSuccess.ShouldBeTrue();
        var order = result.Value;
        order.Id.ShouldBe(5501);
        order.Number.ShouldBe("5501");
        order.Status.ShouldBe("processing");
        order.Currency.ShouldBe("CLP");
        order.Total.ShouldBe(100013.55m);
        order.TotalTax.ShouldBe(15968.55m);
        order.ShippingTotal.ShouldBe(3990m);
        order.DiscountTotal.ShouldBe(8895m);
        order.CustomerId.ShouldBe(4521);
        order.IsGuest.ShouldBeFalse();
        order.CustomerNote.ShouldBe("Entregar en conserjería");
        order.DateCreated.ShouldBe(new DateTimeOffset(2026, 3, 10, 15, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public void Lee_las_direcciones_con_sus_tildes()
    {
        var order = Parse("woocommerce/order-processing.json");

        order.Billing.FirstName.ShouldBe("Valentina");
        order.Billing.LastName.ShouldBe("Muñoz");
        order.Billing.FullName.ShouldBe("Valentina Muñoz");
        order.Billing.Email.ShouldBe("valentina@example.com");
        order.Billing.Phone.ShouldBe("+56912345678");
        order.Billing.Address1.ShouldBe("Av. Providencia 1234");
        order.Billing.Address2.ShouldBe("Depto 56");
        order.Billing.City.ShouldBe("Providencia");
        order.Billing.State.ShouldBe("RM");
        order.Billing.Postcode.ShouldBe("7500000");
        order.Billing.Country.ShouldBe("CL");
        order.Shipping.Address1.ShouldBe("Av. Providencia 1234");
        order.Shipping.Email.ShouldBeEmpty();
    }

    [Fact]
    public void Lee_las_lineas_con_montos_en_texto_y_en_numero()
    {
        var order = Parse("woocommerce/order-processing.json");

        order.LineItems.Count.ShouldBe(2);
        order.LineItems[0].ShouldBe(new WooCommerceLineItem(
            Id: 301,
            ProductId: 88,
            VariationId: 0,
            Name: "Taladro percutor 650 W",
            Sku: "A00001",
            Quantity: 2m,
            Subtotal: 79980m,
            Total: 71982m,
            TotalTax: 13676.58m));
        order.LineItems[1].Sku.ShouldBe("A00010");
        order.LineItems[1].Quantity.ShouldBe(3m);
        order.LineItems[1].Total.ShouldBe(8073m);
    }

    [Fact]
    public void Lee_los_metadatos_del_pedido_incluido_el_RUT_que_agrega_un_plugin()
    {
        var order = Parse("woocommerce/order-processing.json");

        order.GetMeta("_billing_rut").ShouldBe("18.765.432-7");
        order.GetMeta("_wc_order_attribution_session_count").ShouldBe("3");
        order.GetMeta("_no_existe").ShouldBeNull();
    }

    [Fact]
    public void Lee_un_pedido_de_un_invitado_con_empresa_y_sin_datos_opcionales()
    {
        var order = Parse("woocommerce/order-guest.json");

        order.Id.ShouldBe(5502);
        order.Status.ShouldBe("completed");
        order.IsGuest.ShouldBeTrue();
        order.CustomerId.ShouldBe(0);
        order.CustomerNote.ShouldBeNull();
        order.Billing.FullName.ShouldBe("Taller Los Aromos Ltda.");
        order.Billing.Email.ShouldBe("Compras@LosAromos.example.com");
        order.Shipping.City.ShouldBeEmpty();
        order.MetaData.ShouldBeEmpty();
        order.GetMeta("_billing_rut").ShouldBeNull();
        order.Total.ShouldBe(2990m);
        order.LineItems.ShouldHaveSingleItem().Total.ShouldBe(2990m);
    }

    [Fact]
    public void Lee_un_pedido_real_creado_en_WooCommerce_11()
    {
        var order = Parse("woocommerce/order-wc11-real.json");

        order.Id.ShouldBe(14);
        order.Status.ShouldBe("processing");
        order.Currency.ShouldBe("CLP");
        order.Total.ShouldBe(88950m);
        order.TotalTax.ShouldBe(0m);
        order.ShippingTotal.ShouldBe(0m);
        order.IsGuest.ShouldBeTrue();
        order.Billing.Email.ShouldBe("valentina@example.com");
        order.Billing.FullName.ShouldBe("Valentina Muñoz");
        order.GetMeta("_billing_rut").ShouldBe("18.765.432-7");
        order.DateCreated.ShouldNotBeNull();
        order.LineItems.Select(line => (line.Sku, line.Quantity, line.Total)).ShouldBe([("A00001", 2m, 79980m), ("A00010", 3m, 8970m)]);
    }

    [Theory]
    [InlineData("Processing", "processing")]
    [InlineData("COMPLETED", "completed")]
    public void Normaliza_el_estado_a_minusculas(string status, string expected)
    {
        var order = ParseJson($$"""{"id":1,"status":"{{status}}","line_items":[]}""");

        order.Status.ShouldBe(expected);
    }

    [Fact]
    public void Acepta_los_montos_como_numero_o_como_texto()
    {
        var order = ParseJson("""
            {"id":1,"status":"processing","total":100.5,"shipping_total":"20.25","total_tax":0,
             "line_items":[{"id":1,"quantity":"2","subtotal":10,"total":"9.5","total_tax":"1.8"}]}
            """);

        order.Total.ShouldBe(100.5m);
        order.ShippingTotal.ShouldBe(20.25m);
        order.LineItems[0].Quantity.ShouldBe(2m);
        order.LineItems[0].Subtotal.ShouldBe(10m);
        order.LineItems[0].Total.ShouldBe(9.5m);
        order.LineItems[0].TotalTax.ShouldBe(1.8m);
    }

    [Fact]
    public void Tolera_nulos_campos_ausentes_y_tipos_inesperados()
    {
        var order = ParseJson("""
            {"id":"77","status":"processing","customer_id":"12","customer_note":null,"total":null,"billing":null,
             "shipping":"no soy un objeto","meta_data":"tampoco",
             "line_items":[null,{"id":1,"sku":null,"quantity":null},{"id":2,"sku":"X1","quantity":1}]}
            """);

        order.Id.ShouldBe(77);
        order.CustomerId.ShouldBe(12);
        order.CustomerNote.ShouldBeNull();
        order.Total.ShouldBe(0m);
        order.Billing.Email.ShouldBeEmpty();
        order.Shipping.City.ShouldBeEmpty();
        order.MetaData.ShouldBeEmpty();
        order.LineItems.Count.ShouldBe(2);
        order.LineItems[0].Sku.ShouldBeEmpty();
        order.LineItems[0].Quantity.ShouldBe(0m);
        order.LineItems[1].Sku.ShouldBe("X1");
    }

    [Fact]
    public void Sin_numero_visible_usa_el_id()
    {
        ParseJson("""{"id":5501,"status":"processing","line_items":[]}""").Number.ShouldBe("5501");
    }

    [Fact]
    public void Con_un_metadato_repetido_conserva_el_primero()
    {
        var order = ParseJson("""
            {"id":1,"status":"processing","line_items":[],
             "meta_data":[{"key":"_billing_rut","value":"1-9"},{"key":"_billing_rut","value":"2-7"},{"key":"","value":"x"},{"value":"sin clave"}]}
            """);

        order.MetaData.Count.ShouldBe(1);
        order.GetMeta("_billing_rut").ShouldBe("1-9");
    }

    [Fact]
    public void Un_metadato_vacio_se_trata_como_ausente()
    {
        var order = ParseJson("""{"id":1,"status":"processing","line_items":[],"meta_data":[{"key":"_billing_rut","value":"  "}]}""");

        order.GetMeta("_billing_rut").ShouldBeNull();
    }

    [Fact]
    public void Una_fecha_ilegible_queda_sin_valor()
    {
        ParseJson("""{"id":1,"status":"processing","line_items":[],"date_created_gmt":"ayer"}""").DateCreated.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"status":"processing","line_items":[]}""", "id")]
    [InlineData("""{"id":0,"status":"processing","line_items":[]}""", "id")]
    [InlineData("""{"id":-5,"status":"processing","line_items":[]}""", "id")]
    [InlineData("""{"id":"abc","status":"processing","line_items":[]}""", "id")]
    [InlineData("""{"id":1,"line_items":[]}""", "status")]
    [InlineData("""{"id":1,"status":"  ","line_items":[]}""", "status")]
    [InlineData("""{"id":1,"status":"processing"}""", "line_items")]
    [InlineData("""{"id":1,"status":"processing","line_items":{}}""", "line_items")]
    [InlineData("""{"id":1,"status":"processing","line_items":null}""", "line_items")]
    public void Rechaza_un_pedido_al_que_le_faltan_datos_imprescindibles(string json, string missing)
    {
        var result = WooCommerceOrderParser.Parse(json);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("woocommerce.invalid_order");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain(missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es json")]
    [InlineData("{\"id\":1,")]
    [InlineData("webhook_id=123")]
    public void Rechaza_un_cuerpo_que_no_es_JSON(string body)
    {
        var result = WooCommerceOrderParser.Parse(Encoding.UTF8.GetBytes(body));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("woocommerce.invalid_order");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"texto\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Rechaza_un_JSON_que_no_es_un_objeto(string body)
    {
        var result = WooCommerceOrderParser.Parse(body);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("objeto");
    }

    [Fact]
    public void Parse_de_texto_exige_el_cuerpo()
    {
        Should.Throw<ArgumentNullException>(() => WooCommerceOrderParser.Parse((string)null!));
    }

    private static WooCommerceOrder Parse(string fixture)
    {
        var result = WooCommerceOrderParser.Parse(Fixtures.ReadBytes(fixture));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    private static WooCommerceOrder ParseJson(string json)
    {
        var result = WooCommerceOrderParser.Parse(json);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }
}
