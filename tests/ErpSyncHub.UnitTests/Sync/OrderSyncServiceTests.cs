using System.Text;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ErpSyncHub.UnitTests.Sync;

public class OrderSyncServiceTests
{
    private readonly FakeSapGateway _sap = new();
    private readonly FakeMappingStore _mappings = new();
    private readonly FakeLogger<OrderSyncService> _logger = new();

    public OrderSyncServiceTests()
    {
        _sap.AddItem("A00001", "Taladro percutor 650 W");
        _sap.AddItem("A00010", "Huincha de medir 5 m");
    }

    // --- Camino feliz ---

    [Fact]
    public async Task Un_pedido_nuevo_crea_el_cliente_y_la_orden_con_los_datos_de_la_tienda()
    {
        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.AlreadyExisted.ShouldBeFalse();
        result.Value.Order.CardCode.ShouldBe("C18765432-7");

        var customer = _sap.CreatedCustomers.ShouldHaveSingleItem();
        customer.CardCode.ShouldBe("C18765432-7");
        customer.CardName.ShouldBe("Valentina Muñoz");
        customer.TaxId.ShouldBe(Rut("18.765.432-7"));
        customer.Email.ShouldBe("valentina@example.com");
        customer.Phone.ShouldBe("+56912345678");
        customer.Addresses.Count.ShouldBe(2);
        customer.Addresses[0].ShouldBe(new SapAddress("Facturación", SapAddressKind.Billing, "Av. Providencia 1234, Depto 56", "Providencia", "CL", "7500000"));
        customer.Addresses[1].Kind.ShouldBe(SapAddressKind.Shipping);

        var (reference, order, _) = _sap.Orders.ShouldHaveSingleItem();
        reference.ShouldBe("WC-5501");
        order.Reference.ShouldBe("WC-5501");
        order.CardCode.ShouldBe("C18765432-7");
        order.Comments.ShouldBe("Pedido WooCommerce #5501. Nota del cliente: Entregar en conserjería");
        order.DocumentDate.ShouldBe(new DateOnly(2026, 3, 10));
        order.Lines.ShouldBe(
        [
            new NewSapOrderLine("A00001", 2, 35991m),
            new NewSapOrderLine("A00010", 3, 2691m),
        ]);
    }

    [Fact]
    public async Task Guarda_los_mapeos_del_cliente_y_de_los_productos_para_no_consultar_a_SAP_otra_vez()
    {
        await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        _mappings.Customers.ShouldBe(new Dictionary<string, string> { ["customer:4521"] = "C18765432-7" });
        _mappings.Products.ShouldBe(new Dictionary<string, string> { ["A00001"] = "A00001", ["A00010"] = "A00010" });
    }

    [Fact]
    public async Task Procesa_el_pedido_de_un_invitado_con_empresa_sin_RUT()
    {
        var result = await Service().SyncAsync(Job("woocommerce/order-guest.json"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var customer = _sap.CreatedCustomers.ShouldHaveSingleItem();
        customer.CardCode.ShouldBe(SapCardCodes.ForGuest("compras@losaromos.example.com"));
        customer.CardName.ShouldBe("Taller Los Aromos Ltda.");
        customer.TaxId.ShouldBeNull();
        customer.Email.ShouldBe("compras@losaromos.example.com");
        customer.Phone.ShouldBeNull();
        customer.Addresses.ShouldHaveSingleItem().Kind.ShouldBe(SapAddressKind.Billing);
        _sap.LastEmailSearched.ShouldBe("compras@losaromos.example.com");
        _mappings.Customers.ShouldContainKey("email:compras@losaromos.example.com");
        _sap.Orders.ShouldHaveSingleItem().Order.Comments.ShouldBe("Pedido WooCommerce #5502");
    }

    [Fact]
    public async Task Procesa_un_pedido_real_de_WooCommerce_11_de_un_invitado_con_RUT()
    {
        var result = await Service().SyncAsync(Job("woocommerce/order-wc11-real.json", storeTotal: 88950m), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        _sap.CreatedCustomers.ShouldHaveSingleItem().CardCode.ShouldBe("C18765432-7");
        _mappings.Customers.ShouldContainKey("email:valentina@example.com");
        var (reference, order, created) = _sap.Orders.ShouldHaveSingleItem();
        reference.ShouldBe("WC-14");
        order.Lines.ShouldBe([new NewSapOrderLine("A00001", 2, 39990m), new NewSapOrderLine("A00010", 3, 2990m)]);
        created.Total.ShouldBe(88950m);
        _logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    // --- Idempotencia ---

    [Fact]
    public async Task Si_la_orden_ya_existe_en_SAP_no_se_crea_otra_ni_se_toca_nada()
    {
        await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);
        var callsAfterFirstPass = _sap.Calls.Count;

        var second = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        second.IsSuccess.ShouldBeTrue();
        second.Value.AlreadyExisted.ShouldBeTrue();
        second.Value.Order.DocEntry.ShouldBe(1);
        _sap.Orders.ShouldHaveSingleItem();
        _sap.CreatedCustomers.ShouldHaveSingleItem();
        _sap.Calls.Skip(callsAfterFirstPass).ShouldBe([nameof(FakeSapGateway.FindOrderByReferenceAsync)]);
    }

    [Fact]
    public async Task Reanudar_un_job_que_se_corto_antes_de_crear_la_orden_reutiliza_el_cliente_mapeado()
    {
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Transient("sap.unavailable", "SAP cayó."));
        (await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None)).IsFailure.ShouldBeTrue();
        _sap.CreatedCustomers.ShouldHaveSingleItem();
        _sap.ClearFailures();

        var secondPass = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        secondPass.IsSuccess.ShouldBeTrue(secondPass.Error?.Message);
        _sap.CreatedCustomers.ShouldHaveSingleItem();
        _sap.Orders.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reanudar_aunque_se_haya_perdido_el_mapeo_encuentra_al_cliente_en_SAP_por_su_RUT()
    {
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Transient("sap.unavailable", "SAP cayó."));
        await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);
        _sap.ClearFailures();
        _mappings.Customers.Clear();

        var secondPass = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        secondPass.IsSuccess.ShouldBeTrue(secondPass.Error?.Message);
        _sap.CreatedCustomers.ShouldHaveSingleItem();
        secondPass.Value.Order.CardCode.ShouldBe("C18765432-7");
    }

    [Fact]
    public async Task Un_fallo_al_crear_la_orden_conserva_el_cliente_ya_creado_y_su_mapeo()
    {
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Rejected("sap.rejected", "SAP rechazó la orden."));

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        _sap.CreatedCustomers.ShouldHaveSingleItem();
        _mappings.Customers.ShouldContainKey("customer:4521");
        _sap.Orders.ShouldBeEmpty();
    }

    // --- Cliente ---

    [Fact]
    public async Task Un_cliente_con_mapeo_guardado_no_se_busca_en_SAP()
    {
        _mappings.Customers["customer:4521"] = "C-MAPEADO";

        await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.FindCustomerByTaxIdAsync));
        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.FindCustomerByEmailAsync));
        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.EnsureCustomerAsync));
        _sap.Orders.ShouldHaveSingleItem().Order.CardCode.ShouldBe("C-MAPEADO");
    }

    [Theory]
    [InlineData("18.765.432-7")]
    [InlineData("18765432-7")]
    public async Task Un_cliente_que_ya_esta_en_SAP_por_RUT_se_reutiliza_sin_crear_otro(string rutInSap)
    {
        _sap.Customers.Add(new SapCustomer("C-EXISTENTE", "Valentina M.", rutInSap, "otro@example.com", IsActive: true));

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.Value.Order.CardCode.ShouldBe("C-EXISTENTE");
        _sap.CreatedCustomers.ShouldBeEmpty();
        _sap.Calls.ShouldContain(nameof(FakeSapGateway.FindCustomerByTaxIdAsync));
        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.FindCustomerByEmailAsync));
        _mappings.Customers["customer:4521"].ShouldBe("C-EXISTENTE");
    }

    [Fact]
    public async Task Con_RUT_que_no_esta_en_SAP_no_se_adopta_a_un_cliente_solo_por_su_correo()
    {
        _sap.Customers.Add(new SapCustomer("C-OTRO-RUT", "Otro", "11.111.111-1", "valentina@example.com", IsActive: true));

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.Value.Order.CardCode.ShouldBe("C18765432-7");
        _sap.CreatedCustomers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sin_RUT_un_cliente_registrado_se_busca_por_correo_y_si_no_esta_se_crea_con_su_id_de_tienda()
    {
        var order = OrderJson(customerId: 4521, email: "Ana@Example.com", meta: "[]");

        var created = await Service().SyncAsync(Job(order), CancellationToken.None);

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        _sap.LastEmailSearched.ShouldBe("ana@example.com");
        _sap.CreatedCustomers.ShouldHaveSingleItem().CardCode.ShouldBe("WC4521");
    }

    [Fact]
    public async Task Sin_RUT_un_cliente_que_ya_esta_en_SAP_por_correo_se_reutiliza()
    {
        _sap.Customers.Add(new SapCustomer("C-POR-CORREO", "Ana", null, "ana@example.com", IsActive: true));

        var result = await Service().SyncAsync(Job(OrderJson(customerId: 4521, email: "ANA@example.com", meta: "[]")), CancellationToken.None);

        result.Value.Order.CardCode.ShouldBe("C-POR-CORREO");
        _sap.CreatedCustomers.ShouldBeEmpty();
        _mappings.Customers["customer:4521"].ShouldBe("C-POR-CORREO");
    }

    [Fact]
    public async Task Con_la_clave_de_RUT_vacia_se_ignora_el_RUT_del_pedido()
    {
        var service = ServiceWith(_mappings, new OrderSyncOptions { TaxIdMetaKey = string.Empty });

        await service.SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.FindCustomerByTaxIdAsync));
        _sap.CreatedCustomers.ShouldHaveSingleItem().CardCode.ShouldBe("WC4521");
        _sap.CreatedCustomers[0].TaxId.ShouldBeNull();
    }

    [Fact]
    public async Task Un_RUT_con_digito_verificador_incorrecto_detiene_el_pedido_antes_de_tocar_SAP()
    {
        var order = Encoding.UTF8.GetString(Fixtures.ReadBytes("woocommerce/order-processing.json")).Replace("18.765.432-7", "18.765.432-1", StringComparison.Ordinal);

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(OrderSyncErrors.InvalidTaxId);
        result.Error.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Message.ShouldContain("18.765.432-1");
        _sap.CreatedCustomers.ShouldBeEmpty();
        _sap.Orders.ShouldBeEmpty();
        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.FindCustomerByTaxIdAsync));
    }

    [Fact]
    public async Task Un_invitado_sin_correo_no_se_puede_identificar()
    {
        var order = OrderJson(customerId: 0, email: string.Empty, meta: "[]");

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.UnidentifiableCustomer);
        _sap.Orders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_cliente_inhabilitado_en_SAP_se_rechaza_con_su_codigo()
    {
        _sap.Customers.Add(new SapCustomer("C-CONGELADO", "Valentina", "18.765.432-7", null, IsActive: false));

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.InactiveCustomer);
        result.Error.Message.ShouldContain("C-CONGELADO");
        _sap.Orders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_id_de_cliente_demasiado_grande_para_el_CardCode_usa_el_correo()
    {
        var order = OrderJson(customerId: SapCardCodes.MaxStoreCustomerId + 1, email: "ana@example.com", meta: "[]");

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.CreatedCustomers.ShouldHaveSingleItem().CardCode.ShouldBe(SapCardCodes.ForGuest("ana@example.com"));
    }

    [Fact]
    public async Task La_empresa_tiene_prioridad_sobre_la_persona_como_nombre_del_cliente()
    {
        var order = OrderJson(customerId: 7, email: "a@example.com", meta: "[]", company: "Constructora X SpA", firstName: "Ana", lastName: "Pérez");

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.CreatedCustomers.ShouldHaveSingleItem().CardName.ShouldBe("Constructora X SpA");
    }

    [Fact]
    public async Task Los_textos_largos_se_recortan_a_lo_que_admite_SAP()
    {
        var order = OrderJson(
            customerId: 7,
            email: "a@example.com",
            meta: "[]",
            firstName: new string('N', 150),
            phone: new string('9', 40),
            address1: new string('c', 300));

        await Service().SyncAsync(Job(order), CancellationToken.None);

        var customer = _sap.CreatedCustomers.ShouldHaveSingleItem();
        customer.CardName.Length.ShouldBe(100);
        customer.Phone!.Length.ShouldBe(20);
        customer.Addresses[0].Street!.Length.ShouldBe(100);
    }

    // --- Productos ---

    [Fact]
    public async Task Un_SKU_con_mapeo_usa_el_ItemCode_mapeado_sin_consultar_a_SAP()
    {
        _mappings.Products["A00001"] = "ITEM-MAPEADO-1";
        _mappings.Products["A00010"] = "ITEM-MAPEADO-10";

        await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        _sap.Calls.ShouldNotContain(nameof(FakeSapGateway.GetItemAsync));
        _sap.Orders.ShouldHaveSingleItem().Order.Lines.Select(line => line.ItemCode).ShouldBe(["ITEM-MAPEADO-1", "ITEM-MAPEADO-10"]);
    }

    [Fact]
    public async Task Un_SKU_repetido_en_dos_lineas_se_consulta_una_sola_vez()
    {
        var order = OrderWithLines("""
            {"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000"},
            {"id":2,"sku":"A00001","name":"Taladro","quantity":2,"total":"3000"}
            """);

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        _sap.Calls.Count(call => call == nameof(FakeSapGateway.GetItemAsync)).ShouldBe(1);
        _sap.Orders.ShouldHaveSingleItem().Order.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Un_SKU_que_no_esta_mapeado_ni_existe_en_SAP_rechaza_el_pedido_sin_crear_cliente_ni_orden()
    {
        var order = OrderWithLines("""{"id":1,"sku":"NO-EXISTE","name":"Cosa rara","quantity":1,"total":"1000"}""");

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.UnknownSku);
        result.Error.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Message.ShouldContain("NO-EXISTE");
        result.Error.Message.ShouldContain("Cosa rara");
        _sap.CreatedCustomers.ShouldBeEmpty();
        _sap.Orders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_articulo_inhabilitado_en_SAP_rechaza_el_pedido()
    {
        _sap.AddItem("A-CONGELADO", "Viejo", active: false);
        var order = OrderWithLines("""{"id":1,"sku":"A-CONGELADO","name":"Viejo","quantity":1,"total":"1000"}""");

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.InactiveItem);
        _mappings.Products.ShouldBeEmpty();
    }

    [Fact]
    public async Task Una_linea_sin_SKU_se_rechaza_antes_de_llamar_a_SAP()
    {
        var order = OrderWithLines("""{"id":1,"sku":"","name":"Sin SKU","quantity":1,"total":"1000"}""");

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.LineWithoutSku);
        result.Error.Message.ShouldContain("Sin SKU");
        _sap.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Una_cantidad_que_no_es_positiva_se_rechaza_antes_de_llamar_a_SAP(string quantity)
    {
        var order = OrderWithLines($$"""{"id":1,"sku":"A00001","name":"Taladro","quantity":{{quantity}},"total":"1000"}""");

        var result = await Service().SyncAsync(Job(order), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.InvalidQuantity);
        _sap.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_pedido_sin_productos_se_rechaza_antes_de_llamar_a_SAP()
    {
        var result = await Service().SyncAsync(Job(OrderWithLines(string.Empty)), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.WithoutLines);
        _sap.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("100", 3, "33.3333")]
    [InlineData("100", 8, "12.5")]
    [InlineData("0", 2, "0")]
    [InlineData("71982.00", 2, "35991")]
    [InlineData("10.00", 3, "3.3333")]
    [InlineData("20.00", 3, "6.6667")]
    public async Task El_precio_unitario_es_el_total_de_la_linea_con_cupones_dividido_por_la_cantidad(string lineTotal, int quantity, string expectedPrice)
    {
        var order = OrderWithLines($$"""{"id":1,"sku":"A00001","name":"Taladro","quantity":{{quantity}},"total":"{{lineTotal}}","subtotal":"9999"}""");

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem().Order.Lines.ShouldHaveSingleItem().UnitPrice.ShouldBe(decimal.Parse(expectedPrice, System.Globalization.CultureInfo.InvariantCulture));
    }

    // --- Datos de la orden ---

    [Theory]
    [InlineData("2026-03-10T15:04:05", "2026-03-10")] // 12:04 en Santiago
    [InlineData("2026-03-11T02:30:00", "2026-03-10")] // 23:30 del día anterior en Santiago
    [InlineData("2026-03-11T03:00:00", "2026-03-11")] // medianoche exacta en Santiago (UTC-3)
    [InlineData("2026-07-15T03:30:00", "2026-07-14")] // invierno: UTC-4, 23:30 del día anterior
    [InlineData("2026-07-15T04:00:00", "2026-07-15")]
    public async Task La_orden_se_fecha_con_el_dia_en_que_compro_el_cliente_en_Chile(string createdGmt, string expectedDate)
    {
        var order = OrderWithLines("""{"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000"}""", dateCreatedGmt: createdGmt);

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem().Order.DocumentDate.ShouldBe(DateOnly.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Sin_fecha_de_creacion_la_orden_queda_sin_fecha_y_SAP_usa_la_de_hoy()
    {
        var order = OrderWithLines("""{"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000"}""", dateCreatedGmt: null);

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem().Order.DocumentDate.ShouldBeNull();
    }

    [Fact]
    public async Task Las_observaciones_se_recortan_al_maximo_de_SAP()
    {
        var order = OrderWithLines(
            """{"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000"}""",
            customerNote: new string('n', 500));

        await Service().SyncAsync(Job(order), CancellationToken.None);

        _sap.Orders.ShouldHaveSingleItem().Order.Comments!.Length.ShouldBe(254);
    }

    [Fact]
    public void Una_zona_horaria_inexistente_impide_crear_el_servicio()
    {
        Should.Throw<TimeZoneNotFoundException>(() => ServiceWith(_mappings, new OrderSyncOptions { TimeZoneId = "Marte/Olympus" }));
    }

    // --- Totales ---

    [Fact]
    public async Task Si_el_total_de_SAP_difiere_del_de_la_tienda_registra_una_advertencia_con_el_desglose()
    {
        await Service().SyncAsync(Job("woocommerce/order-processing.json", storeTotal: 100013.55m), CancellationToken.None);

        var warning = _logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("5501");
        warning.Message.ShouldContain("100013");
        warning.Message.ShouldContain("80055");
        warning.Message.ShouldContain("15968");
        warning.Message.ShouldContain("3990");
        warning.Message.ShouldContain("8895");
    }

    [Fact]
    public async Task Si_los_totales_coinciden_no_hay_advertencia()
    {
        await Service().SyncAsync(Job("woocommerce/order-guest.json", storeTotal: 2990m), CancellationToken.None);

        _logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task Una_diferencia_de_redondeo_no_genera_advertencia()
    {
        var order = OrderWithLines("""{"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000.005"}""");

        await Service().SyncAsync(Job(order, storeTotal: 1000m), CancellationToken.None);

        _logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task Al_encontrar_una_orden_existente_tambien_compara_los_totales()
    {
        await Service().SyncAsync(Job("woocommerce/order-processing.json", storeTotal: 100013.55m), CancellationToken.None);
        _logger.Collector.Clear();

        await Service().SyncAsync(Job("woocommerce/order-processing.json", storeTotal: 100013.55m), CancellationToken.None);

        _logger.Collector.GetSnapshot().Count(record => record.Level == LogLevel.Warning).ShouldBe(1);
    }

    // --- Fallos de SAP y de datos ---

    [Fact]
    public async Task Un_fallo_pasajero_al_buscar_la_orden_se_devuelve_tal_cual_y_no_se_hace_nada_mas()
    {
        var transient = ResultError.Transient(SapErrors.ServerError, "SAP no pudo atender la solicitud.");
        _sap.FailOn(nameof(FakeSapGateway.FindOrderByReferenceAsync), transient);

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.Error.ShouldBe(transient);
        _sap.Calls.ShouldBe([nameof(FakeSapGateway.FindOrderByReferenceAsync)]);
    }

    [Theory]
    [InlineData(nameof(FakeSapGateway.GetItemAsync))]
    [InlineData(nameof(FakeSapGateway.FindCustomerByTaxIdAsync))]
    [InlineData(nameof(FakeSapGateway.EnsureCustomerAsync))]
    [InlineData(nameof(FakeSapGateway.CreateOrderAsync))]
    public async Task Los_fallos_de_SAP_en_cada_paso_se_propagan_con_su_clasificacion_y_no_crean_la_orden(string operation)
    {
        var error = ResultError.Transient(SapErrors.Unreachable, "Sin conexión.");
        _sap.FailOn(operation, error);

        var result = await Service().SyncAsync(Job("woocommerce/order-processing.json"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
        _sap.Orders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_payload_que_no_es_un_pedido_se_rechaza()
    {
        var result = await Service().SyncAsync(Job("esto no es json"), CancellationToken.None);

        result.Error!.Code.ShouldBe(OrderSyncErrors.InvalidPayload);
        result.Error.Kind.ShouldBe(ErrorKind.Rejected);
        _sap.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Exige_el_job()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Service().SyncAsync(null!, CancellationToken.None));
    }

    // --- Utilidades ---

    private OrderSyncService Service() => ServiceWith(_mappings);

    private OrderSyncService ServiceWith(IMappingStore mappings, OrderSyncOptions? options = null) =>
        new(_sap, mappings, options ?? new OrderSyncOptions(), _logger);

    private static Rut Rut(string value)
    {
        ErpSyncHub.Domain.Customers.Rut.TryParse(value, out var rut).ShouldBeTrue();
        return rut;
    }

    private static ClaimedJob Job(string fixtureOrJson, decimal? storeTotal = null)
    {
        var payload = fixtureOrJson.StartsWith("woocommerce/", StringComparison.Ordinal)
            ? Fixtures.Read(fixtureOrJson)
            : fixtureOrJson;

        return new ClaimedJob(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "worker-1", "woocommerce", "5501", "order", payload, storeTotal);
    }

    private static string OrderJson(
        long customerId,
        string email,
        string meta,
        string company = "",
        string firstName = "Ana",
        string lastName = "Pérez",
        string phone = "",
        string address1 = "Calle 1") =>
        $$"""
        {"id":900,"number":"900","status":"processing","currency":"CLP","total":"1000","customer_id":{{customerId}},
         "date_created_gmt":"2026-03-10T15:00:00",
         "billing":{"first_name":"{{firstName}}","last_name":"{{lastName}}","company":"{{company}}","address_1":"{{address1}}","city":"Santiago","country":"CL","email":"{{email}}","phone":"{{phone}}"},
         "shipping":{},"meta_data":{{meta}},
         "line_items":[{"id":1,"sku":"A00001","name":"Taladro","quantity":1,"total":"1000"}]}
        """;

    private static string OrderWithLines(string lines, string? dateCreatedGmt = "2026-03-10T15:00:00", string customerNote = "") =>
        $$"""
        {"id":901,"number":"901","status":"processing","currency":"CLP","total":"1000","customer_id":0,
         {{(dateCreatedGmt is null ? string.Empty : $"\"date_created_gmt\":\"{dateCreatedGmt}\",")}}
         "customer_note":"{{customerNote}}",
         "billing":{"first_name":"Ana","last_name":"Pérez","email":"ana@example.com"},
         "shipping":{},"meta_data":[],
         "line_items":[{{lines}}]}
        """;
}
