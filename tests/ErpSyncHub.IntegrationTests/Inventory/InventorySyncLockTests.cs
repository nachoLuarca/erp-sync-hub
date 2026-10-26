using ErpSyncHub.Application.Inventory;
using ErpSyncHub.IntegrationTests.Support;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Inventory;

[Collection(PostgresCollectionDefinition.Name)]
public class InventorySyncLockTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Mientras_uno_tiene_el_lock_nadie_mas_lo_obtiene()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);

        await using var first = await TryAcquire(host);
        var second = await TryAcquire(host);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Al_soltarlo_otro_puede_tomarlo()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        var first = await TryAcquire(host);
        first.ShouldNotBeNull();

        await first.DisposeAsync();
        await using var second = await TryAcquire(host);

        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Entre_diez_intentos_simultaneos_solo_uno_lo_obtiene()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TryAcquire(host)));

        try
        {
            attempts.Count(handle => handle is not null).ShouldBe(1);
        }
        finally
        {
            foreach (var handle in attempts.Where(handle => handle is not null))
            {
                await handle!.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Si_la_instancia_muere_la_base_suelta_el_lock_sola()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        await using var held = await TryAcquire(host);
        held.ShouldNotBeNull();
        (await TryAcquire(host)).ShouldBeNull();

        // Simula la caída del proceso: la base termina la sesión que tiene el lock.
        await using (var admin = new NpgsqlConnection(host.ConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid <> pg_backend_pid()",
                admin);
            await kill.ExecuteNonQueryAsync();
        }

        await using var afterCrash = await TryAcquire(host);
        afterCrash.ShouldNotBeNull();
    }

    [Fact]
    public async Task Desechar_un_lock_cuya_conexion_ya_cayo_no_lanza()
    {
        await using var host = await InfrastructureHost.StartAsync(postgres);
        var held = await TryAcquire(host);
        held.ShouldNotBeNull();
        await using (var admin = new NpgsqlConnection(host.ConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid <> pg_backend_pid()",
                admin);
            await kill.ExecuteNonQueryAsync();
        }

        await Should.NotThrowAsync(async () => await held.DisposeAsync());
    }

    private static Task<IAsyncDisposable?> TryAcquire(InfrastructureHost host) =>
        host.UseAsync<IInventorySyncLock, IAsyncDisposable?>(syncLock => syncLock.TryAcquireAsync(CancellationToken.None));
}
