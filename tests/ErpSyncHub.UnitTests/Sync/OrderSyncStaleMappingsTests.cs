using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ErpSyncHub.UnitTests.Sync;

/// <summary>
/// Un mapeo guardado cuyo cliente o artículo ya no existe en SAP se elimina y se resuelve de nuevo, una sola vez dentro del mismo
/// intento, en vez de dejar el job fallido para siempre.
/// </summary>
public class OrderSyncStaleMappingsTests
{
    private const string CustomerKey = "customer:4521"; // el cliente del pedido de ejemplo

    private readonly FakeSapGateway _sap = new() { RejectUnknownReferences = true };
    private readonly FakeMappingStore _mappings = new();
    private readonly FakeLogger<OrderSyncService> _logger = new();

    public OrderSyncStaleMappingsTests()
    {
        _sap.AddItem("A00001");
        _sap.AddItem("A00010");
    }

    private OrderSyncService Service() => new(_sap, _mappings, new OrderSyncOptions(), _logger);

    private static ClaimedJob Job() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "worker-1", "woocommerce", "5501", "order", Fixtures.Read("woocommerce/order-processing.json"), null);

    private int OrderAttempts => _sap.Calls.Count(call => call == nameof(FakeSapGateway.CreateOrderAsync));

    [Fact]
    public async Task Un_cliente_mapeado_que_ya_no_existe_en_SAP_se_elimina_y_se_vuelve_a_resolver_en_el_mismo_intento()
    {
        _mappings.Customers[CustomerKey] = "C99999999-9"; // se borró en SAP

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        OrderAttempts.ShouldBe(2); // la rechazada y la buena
        _sap.CreatedCustomers.ShouldHaveSingleItem().CardCode.ShouldBe("C18765432-7");
        _mappings.Customers[CustomerKey].ShouldBe("C18765432-7"); // el mapeo nuevo reemplazó al obsoleto
        result.Value.Order.CardCode.ShouldBe("C18765432-7");
    }

    [Fact]
    public async Task Un_producto_mapeado_que_ya_no_existe_en_SAP_se_elimina_y_se_vuelve_a_resolver()
    {
        _mappings.Products["A00001"] = "VIEJO-001"; // el artículo se reemplazó en SAP

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        OrderAttempts.ShouldBe(2);
        _mappings.Products["A00001"].ShouldBe("A00001");
        _sap.Orders.ShouldHaveSingleItem().Order.Lines.Select(line => line.ItemCode).ShouldBe(["A00001", "A00010"]);
    }

    [Fact]
    public async Task Deja_una_advertencia_con_el_motivo_y_sin_el_codigo_del_cliente()
    {
        _mappings.Customers[CustomerKey] = "C99999999-9";
        _mappings.Products["A00001"] = "VIEJO-001";

        await Service().SyncAsync(Job(), CancellationToken.None);

        var warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning && record.Message.Contains("mapeos", StringComparison.Ordinal));
        warning.Message.ShouldContain("1 cliente(s)");
        warning.Message.ShouldContain("A00001");
        warning.Message.ShouldNotContain("C99999999");
    }

    [Fact]
    public async Task Solo_se_vuelve_a_resolver_una_vez_aunque_SAP_siga_rechazando()
    {
        _mappings.Customers[CustomerKey] = "C99999999-9";
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Rejected(SapErrors.Rejected, "SAP rechazó la operación (HTTP 400): otro motivo"));

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(SapErrors.Rejected);
        OrderAttempts.ShouldBe(2); // nunca una tercera
    }

    [Fact]
    public async Task Un_rechazo_de_SAP_por_otro_motivo_no_borra_mapeos_que_siguen_vigentes()
    {
        _sap.Customers.Add(new SapCustomer("C12345678-5", "Cliente", null, null, IsActive: true));
        _mappings.Customers[CustomerKey] = "C12345678-5";
        _mappings.Products["A00001"] = "A00001";
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Rejected(SapErrors.Rejected, "SAP rechazó la operación (HTTP 400): otro motivo"));

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        OrderAttempts.ShouldBe(1); // no hubo nada que re-resolver
        _mappings.Customers[CustomerKey].ShouldBe("C12345678-5");
        _mappings.Products["A00001"].ShouldBe("A00001");
    }

    [Fact]
    public async Task Sin_mapeos_guardados_un_rechazo_no_se_reintenta_en_el_mismo_intento()
    {
        _sap.FailOn(nameof(FakeSapGateway.CreateOrderAsync), ResultError.Rejected(SapErrors.Rejected, "SAP rechazó la operación (HTTP 400): otro motivo"));

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        OrderAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Si_SAP_falla_al_comprobar_el_mapeo_el_error_pasajero_se_devuelve_y_el_mapeo_se_conserva()
    {
        _mappings.Customers[CustomerKey] = "C99999999-9";
        _sap.FailOn(nameof(FakeSapGateway.GetCustomerAsync), ResultError.Transient(SapErrors.Unreachable, "Sin conexión."));

        var result = await Service().SyncAsync(Job(), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        _mappings.Customers[CustomerKey].ShouldBe("C99999999-9");
        OrderAttempts.ShouldBe(1);
    }
}
