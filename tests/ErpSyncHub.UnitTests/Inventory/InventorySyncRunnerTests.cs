using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.UnitTests.Inventory;

public class InventorySyncRunnerTests
{
    private readonly FakeSapGateway _sap = new();
    private readonly FakeStoreGateway _store = new();
    private readonly FakeInventorySyncRunStore _runs = new();
    private readonly FakeInventorySyncLock _lock = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero)); // 23:30 del día 9 en Santiago (UTC-3)
    private readonly InventorySyncOptions _options = new() { SapPageSize = 100 };

    private InventorySyncRunner Runner() => Build(_store);

    private InventorySyncRunner Build(IStoreGateway store)
    {
        var service = new InventorySyncService(
            _sap,
            store,
            new FakeMappingStore(),
            new FakeSnapshotStore(),
            _options,
            NullLogger<InventorySyncService>.Instance);

        return new InventorySyncRunner(service, _runs, _lock, store, _options, _time, NullLogger<InventorySyncRunner>.Instance);
    }

    private void Article(string itemCode, decimal stock = 10)
    {
        _sap.AddInventoryItem(itemCode, stock, price: 1000, updatedOn: DateOnly.MaxValue);
        _store.AddProduct(itemCode);
    }

    private Task<Result<InventorySyncRun>> RunAsync(InventorySyncTrigger trigger = InventorySyncTrigger.Scheduled, bool full = false) =>
        Runner().RunAsync(trigger, full, CancellationToken.None);

    [Fact]
    public async Task Sin_ejecuciones_previas_lee_todo_y_deja_el_registro_exitoso_con_sus_contadores()
    {
        Article("A1");
        Article("A2");

        var result = await RunAsync(InventorySyncTrigger.Manual);

        var run = result.Value;
        run.Status.ShouldBe(InventorySyncStatus.Succeeded);
        run.Trigger.ShouldBe(InventorySyncTrigger.Manual);
        run.ChangedSince.ShouldBeNull();
        run.StartedAt.ShouldBe(_time.GetUtcNow());
        run.FinishedAt.ShouldNotBeNull();
        (run.ItemsRead, run.ItemsUpdated, run.ItemsUnchanged, run.ItemsSkipped, run.ItemsFailed).ShouldBe((2, 2, 0, 0, 0));
        _runs.Runs.ShouldHaveSingleItem().ShouldBeSameAs(run);
        _sap.InventoryReads[0].ChangedSince.ShouldBeNull();
    }

    [Fact]
    public async Task La_siguiente_ejecucion_lee_desde_el_dia_local_de_SAP_de_la_ultima_exitosa_menos_un_dia()
    {
        Article("A1");
        await RunAsync();
        _time.Advance(TimeSpan.FromMinutes(15));
        _sap.InventoryReads.Clear();

        var second = (await RunAsync()).Value;

        second.ChangedSince.ShouldBe(new DateOnly(2026, 3, 8));
        _sap.InventoryReads.First().ChangedSince.ShouldBe(new DateOnly(2026, 3, 8));
    }

    [Fact]
    public async Task Una_lectura_completa_ignora_la_fecha_de_corte()
    {
        Article("A1");
        await RunAsync();
        _time.Advance(TimeSpan.FromHours(1));
        _sap.InventoryReads.Clear();

        var run = (await RunAsync(InventorySyncTrigger.Manual, full: true)).Value;

        run.Full.ShouldBeTrue();
        run.ChangedSince.ShouldBeNull();
        _sap.InventoryReads.First().ChangedSince.ShouldBeNull();
    }

    [Fact]
    public async Task Una_ejecucion_con_errores_no_avanza_el_corte()
    {
        Article("A1");
        await RunAsync(); // exitosa a las 23:30 del día 9 en Santiago
        _time.Advance(TimeSpan.FromDays(2));
        _sap.AddInventoryItem("A2", 5, price: 100, updatedOn: DateOnly.MaxValue);
        _store.AddProduct("A2");
        _store.ItemErrors["A2"] = ResultError.Rejected(StoreErrors.Rejected, "No.");
        var withErrors = (await RunAsync()).Value;
        withErrors.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        _time.Advance(TimeSpan.FromHours(1));

        var next = (await RunAsync()).Value;

        next.ChangedSince.ShouldBe(new DateOnly(2026, 3, 8)); // sigue contando desde la exitosa, no desde la que tuvo errores
    }

    [Fact]
    public async Task Una_ejecucion_fallida_registra_el_error_y_lo_que_alcanzo_a_hacer()
    {
        _sap.FailOn(nameof(ISapGateway.GetInventoryPageAsync), ResultError.Transient(SapErrors.Unreachable, "Sin conexión."));

        var result = await RunAsync();

        var run = result.Value;
        run.Status.ShouldBe(InventorySyncStatus.Failed);
        run.ErrorCode.ShouldBe(SapErrors.Unreachable);
        run.FinishedAt.ShouldNotBeNull();
        _runs.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task Una_ejecucion_con_articulos_fallidos_termina_con_errores_pero_completa()
    {
        Article("A1");
        _store.ItemErrors["A1"] = ResultError.Rejected(StoreErrors.Rejected, "No.");

        var run = (await RunAsync()).Value;

        run.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        run.ItemsFailed.ShouldBe(1);
        run.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Sin_tienda_configurada_no_toma_el_lock_ni_registra_nada()
    {
        _store.IsConfigured = false;

        var result = await RunAsync();

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(InventorySyncErrors.StoreNotConfigured);
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
        _lock.Acquired.ShouldBe(0);
        _runs.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Si_otra_ejecucion_tiene_el_lock_no_hace_nada()
    {
        Article("A1");
        _lock.HeldByOther = true;

        var result = await RunAsync();

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(InventorySyncErrors.AlreadyRunning);
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
        _runs.Runs.ShouldBeEmpty();
        _sap.InventoryReads.ShouldBeEmpty();
        _store.UpdateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task El_lock_se_suelta_al_terminar_aunque_la_ejecucion_falle()
    {
        _sap.FailOn(nameof(ISapGateway.GetInventoryPageAsync), ResultError.Transient(SapErrors.Timeout, "Tiempo agotado."));
        await RunAsync();

        _lock.Acquired.ShouldBe(1);
        _lock.Released.ShouldBe(1);
    }

    [Fact]
    public async Task Cierra_como_abandonadas_las_ejecuciones_que_quedaron_en_curso()
    {
        var stale = InventorySyncRun.Start(InventorySyncTrigger.Scheduled, "otra-instancia", full: false, null, _time.GetUtcNow().AddHours(-2)).Value;
        _runs.Runs.Add(stale);
        Article("A1");

        await RunAsync();

        stale.Status.ShouldBe(InventorySyncStatus.Abandoned);
    }

    [Fact]
    public async Task Un_error_inesperado_deja_la_ejecucion_fallida_y_no_se_propaga()
    {
        Article("A1");
        var failing = new ThrowingStore(_store);

        var result = await Build(failing).RunAsync(InventorySyncTrigger.Scheduled, full: false, CancellationToken.None);

        var run = result.Value;
        run.Status.ShouldBe(InventorySyncStatus.Failed);
        run.ErrorCode.ShouldBe(InventorySyncErrors.Unexpected);
        run.ErrorMessage.ShouldBe("base de datos caída");
        _lock.Released.ShouldBe(1);
    }

    [Fact]
    public async Task Si_el_hub_se_detiene_la_ejecucion_queda_registrada_como_fallida_y_se_propaga_la_cancelacion()
    {
        Article("A1");
        using var cts = new CancellationTokenSource();
        _sap.AfterInventoryRead = _ => cts.Cancel();
        _sap.Inventory.Add(_sap.Inventory[0] with { ItemCode = "A2" });
        _options.SapPageSize = 1;

        await Should.ThrowAsync<OperationCanceledException>(() => Runner().RunAsync(InventorySyncTrigger.Scheduled, full: false, cts.Token));

        var run = _runs.Runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(InventorySyncStatus.Failed);
        run.ErrorCode.ShouldBe(InventorySyncErrors.Cancelled);
        _lock.Released.ShouldBe(1);
    }

    /// <summary>Una tienda que lanza una excepción inesperada al buscar productos.</summary>
    private sealed class ThrowingStore(IStoreGateway inner) : IStoreGateway
    {
        public bool IsConfigured => inner.IsConfigured;

        public Task<Result<IReadOnlyDictionary<string, StoreProduct>>> FindProductsBySkuAsync(
            IReadOnlyCollection<string> skus,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("base de datos caída");

        public Task<Result<IReadOnlyList<StoreUpdateOutcome>>> UpdateProductsAsync(
            IReadOnlyCollection<StoreProductUpdate> updates,
            CancellationToken cancellationToken) =>
            inner.UpdateProductsAsync(updates, cancellationToken);
    }
}
