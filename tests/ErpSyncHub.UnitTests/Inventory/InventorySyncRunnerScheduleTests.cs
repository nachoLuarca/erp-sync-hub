using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.UnitTests.Inventory;

/// <summary>La sincronización completa de respaldo: cuándo la periódica deja de ser incremental y pasa a ser completa.</summary>
public class InventorySyncRunnerScheduleTests
{
    private readonly FakeSapGateway _sap = new();
    private readonly FakeStoreGateway _store = new();
    private readonly FakeSnapshotStore _snapshots = new();
    private readonly FakeInventorySyncRunStore _runs = new();
    private readonly FakeInventorySyncLock _lock = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 10, 3, 0, 0, TimeSpan.Zero));
    private readonly InventorySyncOptions _options = new() { SapPageSize = 100, FullInterval = TimeSpan.FromHours(24) };

    public InventorySyncRunnerScheduleTests()
    {
        _sap.AddInventoryItem("A1", inStock: 10, price: 1000, updatedOn: DateOnly.MaxValue);
        _store.AddProduct("A1");
    }

    private InventorySyncRunner Runner()
    {
        var service = new InventorySyncService(_sap, _store, new FakeMappingStore(), _snapshots, _options, NullLogger<InventorySyncService>.Instance);
        return new InventorySyncRunner(service, _runs, _lock, _store, _options, _time, NullLogger<InventorySyncRunner>.Instance);
    }

    private async Task<InventorySyncRun> ScheduledAsync() => (await Runner().RunScheduledAsync(CancellationToken.None)).Value;

    [Fact]
    public async Task La_primera_sincronizacion_periodica_es_completa_y_fija_la_linea_base()
    {
        var run = await ScheduledAsync();

        run.Full.ShouldBeTrue();
        run.Trigger.ShouldBe(InventorySyncTrigger.Scheduled);
        run.ChangedSince.ShouldBeNull();
        run.Status.ShouldBe(InventorySyncStatus.Succeeded);
    }

    [Fact]
    public async Task Despues_de_una_completa_las_periodicas_son_incrementales_hasta_cumplirse_el_intervalo()
    {
        await ScheduledAsync();

        _time.Advance(TimeSpan.FromMinutes(15));
        var soon = await ScheduledAsync();
        _time.Advance(TimeSpan.FromHours(23));
        var almost = await ScheduledAsync();

        soon.Full.ShouldBeFalse();
        soon.ChangedSince.ShouldNotBeNull();
        almost.Full.ShouldBeFalse();
    }

    [Fact]
    public async Task Al_cumplirse_el_intervalo_la_periodica_vuelve_a_ser_completa_y_ignora_la_foto()
    {
        await ScheduledAsync();
        _store.UpdateCalls.Clear();

        _time.Advance(TimeSpan.FromHours(24));
        var run = await ScheduledAsync();

        run.Full.ShouldBeTrue();
        run.ChangedSince.ShouldBeNull();
        run.ItemsUpdated.ShouldBe(1); // la foto decía que nada cambió, pero la completa lo envía igual
        _store.Updates.ShouldHaveSingleItem().Sku.ShouldBe("A1");
    }

    [Fact]
    public async Task El_intervalo_se_mide_desde_la_ultima_completa_y_no_desde_la_ultima_ejecucion()
    {
        await ScheduledAsync(); // completa a las 03:00
        for (var hour = 0; hour < 23; hour++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            (await ScheduledAsync()).Full.ShouldBeFalse();
        }

        _time.Advance(TimeSpan.FromHours(1)); // 24 horas desde la completa, aunque la última ejecución fue hace una hora

        (await ScheduledAsync()).Full.ShouldBeTrue();
    }

    [Fact]
    public async Task Una_completa_manual_exitosa_cuenta_y_posterga_la_siguiente_completa_programada()
    {
        await ScheduledAsync();
        _time.Advance(TimeSpan.FromHours(20));
        (await Runner().RunAsync(InventorySyncTrigger.Manual, full: true, CancellationToken.None)).Value.Status.ShouldBe(InventorySyncStatus.Succeeded);

        _time.Advance(TimeSpan.FromHours(10)); // 30 h desde la programada, pero solo 10 h desde la manual
        var run = await ScheduledAsync();

        run.Full.ShouldBeFalse();
    }

    [Fact]
    public async Task Una_incremental_manual_no_cuenta_como_completa()
    {
        await ScheduledAsync();
        _time.Advance(TimeSpan.FromHours(20));
        await Runner().RunAsync(InventorySyncTrigger.Manual, full: false, CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(4));

        (await ScheduledAsync()).Full.ShouldBeTrue();
    }

    [Fact]
    public async Task Una_completa_que_termino_con_errores_no_cuenta_y_se_reintenta_en_la_siguiente_ejecucion()
    {
        _store.ItemErrors["A1"] = ResultError.Rejected("store.rejected", "No.");
        var failed = await ScheduledAsync();
        failed.Full.ShouldBeTrue();
        failed.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        _store.ItemErrors.Clear();

        _time.Advance(TimeSpan.FromMinutes(15));
        var retried = await ScheduledAsync();

        retried.Full.ShouldBeTrue();
        retried.Status.ShouldBe(InventorySyncStatus.Succeeded);
        _time.Advance(TimeSpan.FromMinutes(15));
        (await ScheduledAsync()).Full.ShouldBeFalse();
    }

    [Fact]
    public async Task Una_completa_fallida_por_un_error_general_tampoco_cuenta()
    {
        _sap.FailOn(nameof(ErpSyncHub.Application.Sap.ISapGateway.GetInventoryPageAsync), ResultError.Transient("sap.unreachable", "Sin conexión."));
        (await ScheduledAsync()).Status.ShouldBe(InventorySyncStatus.Failed);
        _sap.ClearFailures();

        _time.Advance(TimeSpan.FromMinutes(15));

        (await ScheduledAsync()).Full.ShouldBeTrue();
    }

    [Fact]
    public async Task Con_el_intervalo_en_cero_la_completa_programada_esta_desactivada()
    {
        _options.FullInterval = TimeSpan.Zero;

        var first = await ScheduledAsync();
        _time.Advance(TimeSpan.FromDays(30));
        var later = await ScheduledAsync();

        first.Full.ShouldBeFalse();
        later.Full.ShouldBeFalse();
    }

    [Fact]
    public async Task La_sincronizacion_manual_no_depende_del_calendario()
    {
        var incremental = (await Runner().RunAsync(InventorySyncTrigger.Manual, full: false, CancellationToken.None)).Value;
        var full = (await Runner().RunAsync(InventorySyncTrigger.Manual, full: true, CancellationToken.None)).Value;

        incremental.Full.ShouldBeFalse();
        full.Full.ShouldBeTrue();
    }

    [Fact]
    public async Task Si_otra_instancia_tiene_el_lock_no_se_consulta_el_calendario()
    {
        _lock.HeldByOther = true;

        var result = await Runner().RunScheduledAsync(CancellationToken.None);

        result.Error!.Code.ShouldBe(InventorySyncErrors.AlreadyRunning);
        _runs.Runs.ShouldBeEmpty();
        _sap.InventoryReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dos_instancias_que_deciden_una_tras_otra_hacen_una_sola_completa()
    {
        // La decisión se toma con el lock tomado: la segunda instancia ve la completa que acaba de terminar la primera.
        var first = await ScheduledAsync();
        var second = await ScheduledAsync();

        first.Full.ShouldBeTrue();
        second.Full.ShouldBeFalse();
    }

    [Fact]
    public async Task Sin_la_tienda_configurada_no_se_decide_nada()
    {
        _store.IsConfigured = false;

        var result = await Runner().RunScheduledAsync(CancellationToken.None);

        result.Error!.Code.ShouldBe(InventorySyncErrors.StoreNotConfigured);
        _runs.Runs.ShouldBeEmpty();
    }
}
