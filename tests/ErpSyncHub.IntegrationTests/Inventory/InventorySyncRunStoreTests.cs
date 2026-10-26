using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Inventory;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Inventory;

[Collection(PostgresCollectionDefinition.Name)]
public class InventorySyncRunStoreTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static InventorySyncRun NewRun(DateTimeOffset startedAt, InventorySyncTrigger trigger = InventorySyncTrigger.Scheduled) =>
        InventorySyncRun.Start(trigger, "hub-1", full: false, null, startedAt).Value;

    private static Task Add(InfrastructureHost host, InventorySyncRun run) =>
        host.UseAsync<IInventorySyncRunStore>(store => store.AddAsync(run, CancellationToken.None));

    private static Task<InventorySyncRun?> Last(InfrastructureHost host) =>
        host.UseAsync<IInventorySyncRunStore, InventorySyncRun?>(store => store.GetLastAsync(CancellationToken.None));

    [Fact]
    public async Task Sin_ejecuciones_no_hay_ultima_ni_corte()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);

        (await Last(host)).ShouldBeNull();
        (await host.UseAsync<IInventorySyncRunStore, DateTimeOffset?>(store => store.GetLastSucceededStartAsync(CancellationToken.None)))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Una_ejecucion_se_guarda_al_iniciar_y_se_actualiza_al_cerrar()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);

        await host.UseAsync<IInventorySyncRunStore>(async store =>
        {
            var run = NewRun(T0, InventorySyncTrigger.Manual);
            await store.AddAsync(run, CancellationToken.None);

            run.Complete(new InventorySyncCounts(10, 4, 3, 2, 1), T0.AddMinutes(1));
            await store.SaveAsync(run, CancellationToken.None);
        });

        var stored = await Last(host);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(InventorySyncStatus.CompletedWithErrors);
        stored.Trigger.ShouldBe(InventorySyncTrigger.Manual);
        stored.FinishedAt.ShouldBe(T0.AddMinutes(1));
        (stored.ItemsRead, stored.ItemsUpdated, stored.ItemsUnchanged, stored.ItemsSkipped, stored.ItemsFailed).ShouldBe((10, 4, 3, 2, 1));
    }

    [Fact]
    public async Task Guardar_una_ejecucion_leida_en_otro_alcance_tambien_actualiza()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        var run = NewRun(T0);
        await Add(host, run);

        run.Fail(ResultError.Transient("sap.unreachable", "Sin conexión."), default, T0.AddSeconds(5));
        await host.UseAsync<IInventorySyncRunStore>(store => store.SaveAsync(run, CancellationToken.None));

        var stored = await Last(host);
        stored!.Status.ShouldBe(InventorySyncStatus.Failed);
        stored.ErrorCode.ShouldBe("sap.unreachable");
    }

    [Fact]
    public async Task La_ultima_y_las_recientes_van_de_la_mas_nueva_a_la_mas_antigua()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        await Add(host, NewRun(T0));
        await Add(host, NewRun(T0.AddMinutes(30)));
        await Add(host, NewRun(T0.AddMinutes(15)));

        var recent = await host.UseAsync<IInventorySyncRunStore, IReadOnlyList<InventorySyncRun>>(
            store => store.GetRecentAsync(2, CancellationToken.None));

        recent.Select(run => run.StartedAt).ShouldBe([T0.AddMinutes(30), T0.AddMinutes(15)]);
        (await Last(host))!.StartedAt.ShouldBe(T0.AddMinutes(30));
    }

    [Fact]
    public async Task El_corte_es_el_inicio_de_la_ultima_ejecucion_exitosa_y_no_cuentan_las_demas()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        var okOld = NewRun(T0);
        okOld.Complete(new InventorySyncCounts(1, 1, 0, 0, 0), T0.AddMinutes(1));
        var okNew = NewRun(T0.AddHours(1));
        okNew.Complete(new InventorySyncCounts(1, 1, 0, 0, 0), T0.AddHours(1).AddMinutes(1));
        var withErrors = NewRun(T0.AddHours(2));
        withErrors.Complete(new InventorySyncCounts(1, 0, 0, 0, 1), T0.AddHours(2).AddMinutes(1));
        var failed = NewRun(T0.AddHours(3));
        failed.Fail(ResultError.Transient("x", "y"), default, T0.AddHours(3).AddMinutes(1));
        var running = NewRun(T0.AddHours(4));

        foreach (var run in new[] { okOld, okNew, withErrors, failed, running })
        {
            await Add(host, run);
        }

        var cutoff = await host.UseAsync<IInventorySyncRunStore, DateTimeOffset?>(
            store => store.GetLastSucceededStartAsync(CancellationToken.None));

        cutoff.ShouldBe(T0.AddHours(1));
    }

    [Fact]
    public async Task Abandonar_cierra_solo_las_que_estaban_en_curso()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        var done = NewRun(T0);
        done.Complete(default, T0.AddMinutes(1));
        var stale = NewRun(T0.AddHours(1));
        await Add(host, done);
        await Add(host, stale);
        host.Time.Advance(TimeSpan.FromHours(3));

        var closed = await host.UseAsync<IInventorySyncRunStore, int>(store => store.AbandonRunningAsync(CancellationToken.None));
        var again = await host.UseAsync<IInventorySyncRunStore, int>(store => store.AbandonRunningAsync(CancellationToken.None));

        closed.ShouldBe(1);
        again.ShouldBe(0);
        var recent = await host.UseAsync<IInventorySyncRunStore, IReadOnlyList<InventorySyncRun>>(
            store => store.GetRecentAsync(10, CancellationToken.None));
        recent.Single(run => run.Id == stale.Id).Status.ShouldBe(InventorySyncStatus.Abandoned);
        recent.Single(run => run.Id == stale.Id).FinishedAt.ShouldBe(host.Time.GetUtcNow());
        recent.Single(run => run.Id == done.Id).Status.ShouldBe(InventorySyncStatus.Succeeded);
    }

    [Fact]
    public async Task Pedir_cero_ejecuciones_recientes_es_un_error_de_programacion()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => host.UseAsync<IInventorySyncRunStore, IReadOnlyList<InventorySyncRun>>(store => store.GetRecentAsync(0, CancellationToken.None)));
    }
}
