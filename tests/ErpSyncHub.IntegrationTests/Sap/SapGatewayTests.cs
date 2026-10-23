using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.IntegrationTests.Support;
using SapB1.Client.Models;

namespace ErpSyncHub.IntegrationTests.Sap;

/// <summary>El adaptador de SAP contra el SAP simulado: lo que ve el hub cuando todo funciona y cuando SAP rechaza.</summary>
public class SapGatewayTests
{
    private static Rut Rut(string value)
    {
        Domain.Customers.Rut.TryParse(value, out var rut).ShouldBeTrue();
        return rut;
    }

    [Theory]
    [InlineData("12.345.678-5")]
    [InlineData("12345678-5")]
    [InlineData("123456785")]
    public async Task Busca_un_cliente_por_RUT_escrito_de_cualquier_forma(string rut)
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut(rut), CancellationToken.None);

        var customer = result.Value.ShouldNotBeNull();
        customer.CardCode.ShouldBe("C00003");
        customer.CardName.ShouldBe("María Fernández Soto");
        customer.TaxId.ShouldBe("12.345.678-5");
        customer.Email.ShouldBe("maria.fernandez@example.com");
        customer.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Por_defecto_el_RUT_se_busca_con_puntos_y_no_encuentra_al_que_SAP_guarda_sin_ellos()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        await harness.Client.BusinessPartners.CreateAsync(
            new CreateBusinessPartnerRequest { CardCode = "C-SIN-PUNTOS", CardName = "Sin puntos", FederalTaxID = "18765432-7" });

        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("18.765.432-7"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Con_el_formato_sin_puntos_busca_y_encuentra_al_cliente_guardado_sin_puntos()
    {
        using var harness = SapGatewayHarness.AgainstMock(taxIdFormat: "WithoutDots");
        await harness.Client.BusinessPartners.CreateAsync(
            new CreateBusinessPartnerRequest { CardCode = "C-SIN-PUNTOS", CardName = "Sin puntos", FederalTaxID = "18765432-7" });

        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("18.765.432-7"), CancellationToken.None);

        result.Value.ShouldNotBeNull().CardCode.ShouldBe("C-SIN-PUNTOS");
    }

    [Fact]
    public async Task Con_el_formato_sin_puntos_ya_no_encuentra_a_los_clientes_guardados_con_puntos()
    {
        using var harness = SapGatewayHarness.AgainstMock(taxIdFormat: "WithoutDots");

        // C00003 está en el SAP simulado como 12.345.678-5.
        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("12.345.678-5"), CancellationToken.None);

        result.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData("WithDots", "18.765.432-7")]
    [InlineData("WithoutDots", "18765432-7")]
    [InlineData("withoutdots", "18765432-7")]
    public async Task Al_crear_un_cliente_el_RUT_se_envia_en_el_formato_configurado(string format, string expectedInSap)
    {
        using var harness = SapGatewayHarness.AgainstMock(taxIdFormat: format);
        var customer = new NewSapCustomer("C18765432-7", "Valentina", Rut("18765432-7"), null, null, []);

        var created = await harness.Gateway.EnsureCustomerAsync(customer, CancellationToken.None);

        created.IsSuccess.ShouldBeTrue();
        created.Value.TaxId.ShouldBe(expectedInSap);
        (await harness.Client.BusinessPartners.GetAsync("C18765432-7")).ShouldNotBeNull().FederalTaxID.ShouldBe(expectedInSap);
    }

    [Theory]
    [InlineData("WithDots")]
    [InlineData("WithoutDots")]
    public async Task Lo_que_el_hub_crea_siempre_lo_encuentra_en_el_mismo_formato(string format)
    {
        using var harness = SapGatewayHarness.AgainstMock(taxIdFormat: format);
        await harness.Gateway.EnsureCustomerAsync(
            new NewSapCustomer("C18765432-7", "Valentina", Rut("18.765.432-7"), null, null, []),
            CancellationToken.None);

        var found = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("18765432-7"), CancellationToken.None);

        found.Value.ShouldNotBeNull().CardCode.ShouldBe("C18765432-7");
    }

    [Fact]
    public async Task Un_RUT_que_no_esta_en_SAP_devuelve_exito_sin_cliente()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("11.111.111-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task La_busqueda_por_RUT_ignora_a_los_proveedores()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        // P00001 es un proveedor con RUT 76.987.654-5: no sirve para una orden de venta.
        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("76.987.654-5"), CancellationToken.None);

        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Si_hay_varios_clientes_con_el_mismo_RUT_elige_siempre_el_de_menor_codigo()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        await harness.Client.BusinessPartners.CreateAsync(
            new CreateBusinessPartnerRequest { CardCode = "A-DUPLICADO", CardName = "Duplicado", FederalTaxID = "12.345.678-5" });

        var result = await harness.Gateway.FindCustomerByTaxIdAsync(Rut("12.345.678-5"), CancellationToken.None);

        result.Value.ShouldNotBeNull().CardCode.ShouldBe("A-DUPLICADO");
    }

    [Fact]
    public async Task Busca_un_cliente_por_correo()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var found = await harness.Gateway.FindCustomerByEmailAsync("taller.rojas@example.com", CancellationToken.None);
        var missing = await harness.Gateway.FindCustomerByEmailAsync("nadie@example.com", CancellationToken.None);
        var supplier = await harness.Gateway.FindCustomerByEmailAsync("pedidos@herramientasdelsur.example.com", CancellationToken.None);

        found.Value.ShouldNotBeNull().CardCode.ShouldBe("C00005");
        missing.Value.ShouldBeNull();
        supplier.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Un_correo_con_comilla_no_altera_la_consulta()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.FindCustomerByEmailAsync("x' or CardCode ne 'zzz", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Obtiene_un_cliente_por_su_codigo_o_devuelve_exito_sin_cliente()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var found = await harness.Gateway.GetCustomerAsync("C00001", CancellationToken.None);
        var missing = await harness.Gateway.GetCustomerAsync("NO-EXISTE", CancellationToken.None);

        found.Value.ShouldNotBeNull().CardName.ShouldBe("Constructora Los Andes SpA");
        missing.IsSuccess.ShouldBeTrue();
        missing.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Un_cliente_congelado_se_informa_como_inactivo()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        await harness.Client.BusinessPartners.UpdateAsync("C00001", new UpdateBusinessPartnerRequest { Frozen = YesNo.Yes });

        var result = await harness.Gateway.GetCustomerAsync("C00001", CancellationToken.None);

        result.Value.ShouldNotBeNull().IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Crea_un_cliente_nuevo_con_el_RUT_con_puntos_y_sus_direcciones()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var customer = new NewSapCustomer(
            "C18765432-7",
            "Valentina Muñoz",
            Rut("18765432-7"),
            "valentina@example.com",
            "+56912345678",
            [
                new SapAddress("Facturación", SapAddressKind.Billing, "Av. Providencia 1234", "Providencia", "CL", "7500000"),
                new SapAddress("Despacho", SapAddressKind.Shipping, "Av. Providencia 1234", "Providencia", "CL", "7500000"),
            ]);

        var result = await harness.Gateway.EnsureCustomerAsync(customer, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.CardCode.ShouldBe("C18765432-7");
        result.Value.TaxId.ShouldBe("18.765.432-7");
        var stored = await harness.Client.BusinessPartners.GetAsync("C18765432-7");
        stored.ShouldNotBeNull();
        stored.CardType.ShouldBe(CardTypes.Customer);
        stored.CardName.ShouldBe("Valentina Muñoz");
        stored.EmailAddress.ShouldBe("valentina@example.com");
        stored.Phone1.ShouldBe("+56912345678");
        stored.BPAddresses.Select(address => address.AddressType).ShouldBe([AddressTypes.BillTo, AddressTypes.ShipTo]);
        stored.BPAddresses[0].ZipCode.ShouldBe("7500000");
    }

    [Fact]
    public async Task Crear_dos_veces_el_mismo_cliente_devuelve_el_que_ya_existe_sin_error()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var customer = new NewSapCustomer("WC4521", "Cliente Web", null, "web@example.com", null, []);

        var first = await harness.Gateway.EnsureCustomerAsync(customer, CancellationToken.None);
        var second = await harness.Gateway.EnsureCustomerAsync(customer with { CardName = "Otro nombre" }, CancellationToken.None);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.CardCode.ShouldBe("WC4521");
        second.Value.CardName.ShouldBe("Cliente Web"); // el existente no se pisa
    }

    [Fact]
    public async Task Un_cliente_con_datos_invalidos_se_rechaza_de_forma_definitiva()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.EnsureCustomerAsync(new NewSapCustomer("WC1", "   ", null, null, null, []), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Code.ShouldBe(SapErrors.Rejected);
        result.Error.Message.ShouldContain("CardName");
    }

    [Fact]
    public async Task Obtiene_un_articulo_y_distingue_el_que_no_existe()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var found = await harness.Gateway.GetItemAsync("A00001", CancellationToken.None);
        var missing = await harness.Gateway.GetItemAsync("ZZZ", CancellationToken.None);

        found.Value.ShouldBe(new SapItem("A00001", "Taladro percutor 650 W", IsActive: true));
        missing.IsSuccess.ShouldBeTrue();
        missing.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Crea_la_orden_y_devuelve_su_referencia()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var order = new NewSapOrder(
            "C00001",
            "WOO-5501",
            "Pedido web",
            new DateOnly(2026, 3, 10),
            [new NewSapOrderLine("A00001", 2, 35991), new NewSapOrderLine("A00010", 3, 2691)]);

        var result = await harness.Gateway.CreateOrderAsync(order, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DocEntry.ShouldBe(1);
        result.Value.DocNum.ShouldBe(1001);
        result.Value.CardCode.ShouldBe("C00001");
        result.Value.Total.ShouldBe((2 * 35991m) + (3 * 2691m));
        var stored = await harness.Client.Orders.GetAsync(1);
        stored.ShouldNotBeNull();
        stored.NumAtCard.ShouldBe("WOO-5501");
        stored.Comments.ShouldBe("Pedido web");
        stored.DocDate.ShouldBe(new DateOnly(2026, 3, 10));
        stored.DocumentLines.Select(line => line.UnitPrice).ShouldBe([35991m, 2691m]);
    }

    [Fact]
    public async Task Encuentra_la_orden_de_un_pedido_por_su_referencia()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        var created = await harness.Gateway.CreateOrderAsync(
            new NewSapOrder("C00001", "WOO-5501", null, null, [new NewSapOrderLine("A00001", 1, 100)]),
            CancellationToken.None);

        var found = await harness.Gateway.FindOrderByReferenceAsync("WOO-5501", CancellationToken.None);
        var missing = await harness.Gateway.FindOrderByReferenceAsync("WOO-9999", CancellationToken.None);

        found.Value.ShouldBe(created.Value);
        missing.IsSuccess.ShouldBeTrue();
        missing.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Consultar_si_existe_una_orden_cuando_aun_no_hay_ninguna_no_falla()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.FindOrderByReferenceAsync("WOO-1", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData("NO-EXISTE", "A00001", 1, "Business partner 'NO-EXISTE' does not exist")]
    [InlineData("P00001", "A00001", 1, "vendor")]
    [InlineData("C00001", "ZZZ", 1, "Item 'ZZZ' does not exist")]
    [InlineData("C00001", "A00001", 0, "Quantity must be greater than zero")]
    public async Task Una_orden_que_SAP_rechaza_es_un_error_definitivo_con_el_motivo_de_SAP(
        string cardCode, string itemCode, int quantity, string expectedSapMessage)
    {
        using var harness = SapGatewayHarness.AgainstMock();

        var result = await harness.Gateway.CreateOrderAsync(
            new NewSapOrder(cardCode, "WOO-1", null, null, [new NewSapOrderLine(itemCode, quantity, 100)]),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.IsTransient.ShouldBeFalse();
        result.Error.Code.ShouldBe(SapErrors.Rejected);
        result.Error.Message.ShouldContain("HTTP 400");
        result.Error.Message.ShouldContain("-10");
        result.Error.Message.ShouldContain(expectedSapMessage);
    }

    [Fact]
    public async Task Una_orden_a_un_cliente_congelado_se_rechaza()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        await harness.Client.BusinessPartners.UpdateAsync("C00001", new UpdateBusinessPartnerRequest { Frozen = YesNo.Yes });

        var result = await harness.Gateway.CreateOrderAsync(
            new NewSapOrder("C00001", "WOO-1", null, null, [new NewSapOrderLine("A00001", 1, 100)]),
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Message.ShouldContain("inactive");
    }

    [Fact]
    public async Task Credenciales_incorrectas_son_un_error_de_sesion_pasajero()
    {
        using var harness = SapGatewayHarness.AgainstMock(password: "incorrecta");

        var result = await harness.Gateway.GetItemAsync("A00001", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.IsTransient.ShouldBeTrue();
        result.Error.Code.ShouldBe(SapErrors.Session);
        result.Error.Message.ShouldContain("-304");
    }

    [Fact]
    public async Task Una_sesion_expirada_se_renueva_sin_que_el_hub_se_entere()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        (await harness.Gateway.GetItemAsync("A00001", CancellationToken.None)).IsSuccess.ShouldBeTrue();
        await harness.Mock.ExpireSessionsAsync();

        var result = await harness.Gateway.GetItemAsync("A00002", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull().ItemCode.ShouldBe("A00002");
    }

    [Fact]
    public async Task Una_peticion_cancelada_se_propaga_y_no_se_convierte_en_error_de_SAP()
    {
        using var harness = SapGatewayHarness.AgainstMock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => harness.Gateway.GetItemAsync("A00001", cts.Token));
    }

    [Fact]
    public async Task Los_argumentos_obligatorios_se_validan()
    {
        using var harness = SapGatewayHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentNullException>(() => harness.Gateway.EnsureCustomerAsync(null!, CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(() => harness.Gateway.CreateOrderAsync(null!, CancellationToken.None));
    }
}
