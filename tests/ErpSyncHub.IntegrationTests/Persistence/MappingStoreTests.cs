using ErpSyncHub.Application.Sync;
using ErpSyncHub.Domain.Mappings;
using ErpSyncHub.Infrastructure;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class MappingStoreTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Un_producto_sin_mapeo_devuelve_null()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        (await host.UseAsync(store => store.FindItemCodeAsync("TAL-650", CancellationToken.None))).ShouldBeNull();
    }

    [Fact]
    public async Task Un_mapeo_de_producto_guardado_se_encuentra_aunque_el_SKU_traiga_espacios()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await host.UseAsync(store => store.SaveProductMappingAsync("  TAL-650 ", "A00001", CancellationToken.None));

        (await host.UseAsync(store => store.FindItemCodeAsync("TAL-650", CancellationToken.None))).ShouldBe("A00001");
        (await host.UseAsync(store => store.FindItemCodeAsync(" TAL-650  ", CancellationToken.None))).ShouldBe("A00001");
        (await host.CountAsync("product_mappings")).ShouldBe(1);
    }

    [Fact]
    public async Task Un_mapeo_de_cliente_guardado_se_encuentra()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await host.UseAsync(store => store.SaveCustomerMappingAsync(CustomerKey.ForRegistered(4521), "C18765432-7", CancellationToken.None));

        (await host.UseAsync(store => store.FindCardCodeAsync("customer:4521", CancellationToken.None))).ShouldBe("C18765432-7");
        (await host.UseAsync(store => store.FindCardCodeAsync("customer:9999", CancellationToken.None))).ShouldBeNull();
    }

    [Fact]
    public async Task Guardar_dos_veces_el_mismo_mapeo_no_falla_y_no_pisa_el_primero()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await host.UseAsync(store => store.SaveProductMappingAsync("TAL-650", "A00001", CancellationToken.None));
        await host.UseAsync(store => store.SaveProductMappingAsync("TAL-650", "A00002", CancellationToken.None));
        await host.UseAsync(store => store.SaveCustomerMappingAsync("customer:1", "C-PRIMERO", CancellationToken.None));
        await host.UseAsync(store => store.SaveCustomerMappingAsync("customer:1", "C-SEGUNDO", CancellationToken.None));

        (await host.UseAsync(store => store.FindItemCodeAsync("TAL-650", CancellationToken.None))).ShouldBe("A00001");
        (await host.UseAsync(store => store.FindCardCodeAsync("customer:1", CancellationToken.None))).ShouldBe("C-PRIMERO");
        (await host.CountAsync("product_mappings")).ShouldBe(1);
        (await host.CountAsync("customer_mappings")).ShouldBe(1);
    }

    [Fact]
    public async Task Veinte_workers_guardando_el_mismo_mapeo_a_la_vez_dejan_una_sola_fila()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await host.UseAsync(store => store.SaveProductMappingAsync("TAL-650", $"A{i:D5}", CancellationToken.None));
            await host.UseAsync(store => store.SaveCustomerMappingAsync("customer:1", $"C{i:D5}", CancellationToken.None));
        })));

        (await host.CountAsync("product_mappings")).ShouldBe(1);
        (await host.CountAsync("customer_mappings")).ShouldBe(1);
    }

    [Theory]
    [InlineData("", "A00001")]
    [InlineData("TAL-650", "")]
    public async Task Un_mapeo_de_producto_invalido_se_rechaza(string sku, string itemCode)
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await Should.ThrowAsync<ArgumentException>(
            () => host.UseAsync(store => store.SaveProductMappingAsync(sku, itemCode, CancellationToken.None)));
    }

    [Fact]
    public async Task Un_mapeo_de_cliente_invalido_se_rechaza()
    {
        await using var host = await StoreHost.StartAsync(postgres);

        await Should.ThrowAsync<ArgumentException>(
            () => host.UseAsync(store => store.SaveCustomerMappingAsync("customer:1", new string('c', 16), CancellationToken.None)));
    }

    private sealed class StoreHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _connectionString;

        private StoreHost(ServiceProvider provider, string connectionString)
        {
            _provider = provider;
            _connectionString = connectionString;
        }

        public static async Task<StoreHost> StartAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = connectionString,
                    ["SAP:BaseUrl"] = "http://localhost",
                    ["SAP:CompanyDb"] = "x",
                    ["SAP:UserName"] = "x",
                    ["SAP:Password"] = "x",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInfrastructure(configuration);
            return new StoreHost(services.BuildServiceProvider(), connectionString);
        }

        public async Task<T> UseAsync<T>(Func<IMappingStore, Task<T>> action)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IMappingStore>());
        }

        public async Task UseAsync(Func<IMappingStore, Task> action)
        {
            await using var scope = _provider.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IMappingStore>());
        }

        public async Task<long> CountAsync(string table)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
