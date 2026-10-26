using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.UnitTests.Domain;

public class InventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(2);

    [Fact]
    public void ItemSnapshot_se_crea_con_lo_confirmado_en_la_tienda()
    {
        var result = ItemSnapshot.Create("TAL-650", "A00001", 45, 39990m, Now);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Sku.ShouldBe("TAL-650");
        result.Value.ItemCode.ShouldBe("A00001");
        result.Value.StockQuantity.ShouldBe(45);
        result.Value.Price.ShouldBe(39990m);
        result.Value.SyncedAt.ShouldBe(Now);
        result.Value.Id.Version.ShouldBe(7);
    }

    [Theory]
    [InlineData("", "A00001", 1, "snapshot.invalid_sku")]
    [InlineData(" ", "A00001", 1, "snapshot.invalid_sku")]
    [InlineData("TAL-650", "", 1, "snapshot.invalid_item_code")]
    [InlineData("TAL-650", "A00001", -1, "snapshot.invalid_stock")]
    public void ItemSnapshot_rechaza_datos_invalidos(string sku, string itemCode, int stock, string code)
    {
        var result = ItemSnapshot.Create(sku, itemCode, stock, 1m, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(code);
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void ItemSnapshot_rechaza_valores_demasiado_largos()
    {
        ItemSnapshot.Create(new string('s', ItemSnapshot.MaxSkuLength + 1), "A00001", 1, null, Now).IsFailure.ShouldBeTrue();
        ItemSnapshot.Create("TAL-650", new string('i', ItemSnapshot.MaxItemCodeLength + 1), 1, null, Now).IsFailure.ShouldBeTrue();
        ItemSnapshot.Create(new string('s', ItemSnapshot.MaxSkuLength), new string('i', ItemSnapshot.MaxItemCodeLength), 1, null, Now)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ItemSnapshot_Confirm_conserva_lo_que_no_se_envio()
    {
        var snapshot = ItemSnapshot.Create("TAL-650", "A00001", 45, 39990m, Now).Value;

        snapshot.Confirm("A00001", stockQuantity: 40, price: null, Later);

        snapshot.StockQuantity.ShouldBe(40);
        snapshot.Price.ShouldBe(39990m);
        snapshot.SyncedAt.ShouldBe(Later);

        snapshot.Confirm("A00001", stockQuantity: null, price: 41990m, Later.AddMinutes(1));

        snapshot.StockQuantity.ShouldBe(40);
        snapshot.Price.ShouldBe(41990m);
    }

    [Fact]
    public void ItemSnapshot_Confirm_sigue_al_SKU_si_pasa_a_otro_articulo()
    {
        var snapshot = ItemSnapshot.Create("TAL-650", "A00001", 45, null, Now).Value;

        snapshot.Confirm("A00002", stockQuantity: 3, price: null, Later);

        snapshot.ItemCode.ShouldBe("A00002");
    }

    [Fact]
    public void La_ejecucion_nace_en_curso_con_los_contadores_en_cero()
    {
        var result = InventorySyncRun.Start(InventorySyncTrigger.Scheduled, "hub-1", full: false, new DateOnly(2026, 3, 9), Now);

        result.IsSuccess.ShouldBeTrue();
        var run = result.Value;
        run.Status.ShouldBe(InventorySyncStatus.Running);
        run.Trigger.ShouldBe(InventorySyncTrigger.Scheduled);
        run.Instance.ShouldBe("hub-1");
        run.ChangedSince.ShouldBe(new DateOnly(2026, 3, 9));
        run.StartedAt.ShouldBe(Now);
        run.FinishedAt.ShouldBeNull();
        (run.ItemsRead + run.ItemsUpdated + run.ItemsUnchanged + run.ItemsSkipped + run.ItemsFailed).ShouldBe(0);
        run.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void Una_ejecucion_completa_no_tiene_fecha_de_corte()
    {
        var run = InventorySyncRun.Start(InventorySyncTrigger.Manual, "hub-1", full: true, new DateOnly(2026, 3, 9), Now).Value;

        run.Full.ShouldBeTrue();
        run.ChangedSince.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void La_ejecucion_exige_una_instancia(string instance)
    {
        var result = InventorySyncRun.Start(InventorySyncTrigger.Manual, instance, full: false, null, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("inventory_run.invalid_instance");
    }

    [Fact]
    public void La_ejecucion_sin_articulos_fallidos_termina_exitosa()
    {
        var run = NewRun();

        var closed = run.Complete(new InventorySyncCounts(Read: 10, Updated: 4, Unchanged: 5, Skipped: 1, Failed: 0), Later);

        closed.IsSuccess.ShouldBeTrue();
        run.Status.ShouldBe(InventorySyncStatus.Succeeded);
        run.FinishedAt.ShouldBe(Later);
        (run.ItemsRead, run.ItemsUpdated, run.ItemsUnchanged, run.ItemsSkipped, run.ItemsFailed).ShouldBe((10, 4, 5, 1, 0));
    }

    [Fact]
    public void La_ejecucion_con_articulos_fallidos_termina_con_errores()
    {
        var run = NewRun();

        run.Complete(new InventorySyncCounts(Read: 3, Updated: 1, Unchanged: 0, Skipped: 0, Failed: 2), Later).IsSuccess.ShouldBeTrue();

        run.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        run.ItemsFailed.ShouldBe(2);
    }

    [Fact]
    public void La_ejecucion_rechaza_contadores_que_no_suman_lo_leido()
    {
        var run = NewRun();

        var closed = run.Complete(new InventorySyncCounts(Read: 10, Updated: 4, Unchanged: 5, Skipped: 0, Failed: 0), Later);

        closed.IsFailure.ShouldBeTrue();
        closed.Error!.Code.ShouldBe("inventory_run.inconsistent_counts");
        run.Status.ShouldBe(InventorySyncStatus.Running);
    }

    [Fact]
    public void La_ejecucion_fallida_conserva_el_error_y_lo_que_alcanzo_a_hacer()
    {
        var run = NewRun();

        var closed = run.Fail(
            ResultError.Transient("sap.unreachable", "No se pudo conectar con SAP."),
            new InventorySyncCounts(Read: 20, Updated: 5, Unchanged: 0, Skipped: 0, Failed: 0),
            Later);

        closed.IsSuccess.ShouldBeTrue();
        run.Status.ShouldBe(InventorySyncStatus.Failed);
        run.ErrorCode.ShouldBe("sap.unreachable");
        run.ErrorMessage.ShouldBe("No se pudo conectar con SAP.");
        run.ItemsRead.ShouldBe(20);
        run.ItemsUpdated.ShouldBe(5);
        run.FinishedAt.ShouldBe(Later);
    }

    [Fact]
    public void La_ejecucion_fallida_trunca_el_mensaje_demasiado_largo()
    {
        var run = NewRun();

        run.Fail(ResultError.Rejected("x", new string('m', InventorySyncRun.MaxErrorMessageLength + 50)), default, Later);

        run.ErrorMessage!.Length.ShouldBe(InventorySyncRun.MaxErrorMessageLength);
    }

    [Fact]
    public void Una_ejecucion_cerrada_no_se_puede_cerrar_otra_vez()
    {
        var run = NewRun();
        run.Complete(default, Later);

        var again = run.Fail(ResultError.Transient("x", "y"), default, Later);

        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe("inventory_run.already_closed");
        again.Error.Kind.ShouldBe(ErrorKind.Conflict);
        run.Status.ShouldBe(InventorySyncStatus.Succeeded);
    }

    [Fact]
    public void Los_contadores_negativos_son_invalidos()
    {
        new InventorySyncCounts(-1, 0, 0, 0, 0).IsConsistent.ShouldBeFalse();
        new InventorySyncCounts(0, 0, 0, 0, 0).IsConsistent.ShouldBeTrue();
        NewRun().Fail(ResultError.Transient("x", "y"), new InventorySyncCounts(-1, 0, 0, 0, 0), Later).IsFailure.ShouldBeTrue();
    }

    private static InventorySyncRun NewRun() =>
        InventorySyncRun.Start(InventorySyncTrigger.Scheduled, "hub-1", full: false, null, Now).Value;
}
