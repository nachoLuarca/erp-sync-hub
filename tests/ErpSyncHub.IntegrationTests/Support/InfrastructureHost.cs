using ErpSyncHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// La infraestructura del hub tal como la registra <c>AddInfrastructure</c>, sobre una base de datos migrada y con un reloj
/// controlable. Cada <c>UseAsync</c> trabaja en un alcance nuevo, como un job o una petición.
/// </summary>
internal sealed class InfrastructureHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private InfrastructureHost(ServiceProvider provider, string connectionString, FakeTimeProvider time)
    {
        _provider = provider;
        ConnectionString = connectionString;
        Time = time;
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Time { get; }

    public static async Task<InfrastructureHost> StartAsync(PostgresFixture postgres, DateTimeOffset? now = null)
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

        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddInfrastructure(configuration);
        return new InfrastructureHost(services.BuildServiceProvider(), connectionString, time);
    }

    public async Task<T> UseAsync<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task UseAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = _provider.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
