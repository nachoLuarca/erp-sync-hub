using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging.Testing;

namespace ErpSyncHub.UnitTests.Sync;

/// <summary>Mientras se procesa un pedido, sus datos personales se ocultan en los mensajes de los sistemas externos.</summary>
public class OrderSyncPersonalDataTests
{
    private static ClaimedJob Claim() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "worker-1", SyncSources.WooCommerce, "5501", SyncJobTypes.Order, Fixtures.Read("woocommerce/order-processing.json"), 100013.55m);

    private static OrderSyncService Service(FakeSapGateway sap) =>
        new(sap, new FakeMappingStore(), new OrderSyncOptions(), new FakeLogger<OrderSyncService>());

    [Fact]
    public async Task Durante_la_sincronizacion_el_nombre_la_direccion_el_telefono_el_correo_y_el_RUT_del_pedido_se_ocultan()
    {
        var sap = new FakeSapGateway();
        var seen = new List<string?>();
        sap.OnCall = _ => seen.Add(PersonalDataScope.Redact(
            "Valentina Muñoz | AV. PROVIDENCIA 1234 | depto 56 | +56 9 1234 5678 | VALENTINA@example.com | 18.765.432-7 | 187654327"));

        await Service(sap).SyncAsync(Claim(), CancellationToken.None);

        seen.ShouldNotBeEmpty();
        seen.ShouldAllBe(text => text == "[nombre] | [dirección] | [dirección] | [teléfono] | [correo] | [RUT] | [RUT]");
    }

    [Fact]
    public async Task Al_terminar_la_sincronizacion_el_ambito_se_cierra()
    {
        await Service(new FakeSapGateway()).SyncAsync(Claim(), CancellationToken.None);

        PersonalDataScope.Redact("Valentina Muñoz").ShouldBe("Valentina Muñoz");
    }

    [Fact]
    public async Task El_ambito_se_cierra_tambien_si_la_sincronizacion_falla()
    {
        var sap = new FakeSapGateway();
        sap.FailOn(nameof(FakeSapGateway.FindOrderByReferenceAsync), ErpSyncHub.Domain.Common.ResultError.Transient("sap.unreachable", "Sin conexión."));

        await Service(sap).SyncAsync(Claim(), CancellationToken.None);

        PersonalDataScope.Redact("Valentina Muñoz").ShouldBe("Valentina Muñoz");
    }
}
