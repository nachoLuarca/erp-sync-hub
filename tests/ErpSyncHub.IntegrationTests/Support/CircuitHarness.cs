using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Domain.Resilience;
using ErpSyncHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// Una instancia del hub vista solo por su circuit breaker: el servicio y su almacén tal como los usa la aplicación, con un reloj
/// controlable. Dos arneses sobre la misma base de datos son dos instancias que comparten el circuito.
/// </summary>
internal sealed class CircuitHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    private CircuitHarness(ServiceProvider provider, FakeTimeProvider time, CapturedLogs logs)
    {
        _provider = provider;
        Time = time;
        Logs = logs;
    }

    public FakeTimeProvider Time { get; }

    /// <summary>Lo que escribió el servicio en el log.</summary>
    public CapturedLogs Logs { get; }

    public static CircuitHarness For(string connectionString, Action<CircuitBreakerOptions>? configure = null, FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = connectionString })
            .Build();
        var logs = new CapturedLogs();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddInfrastructure(configuration);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(time);

        var options = new CircuitBreakerOptions();
        configure?.Invoke(options);
        services.RemoveAll<CircuitBreakerOptions>();
        services.AddSingleton(options);

        return new CircuitHarness(services.BuildServiceProvider(), time, logs);
    }

    /// <summary>Ejecuta algo con un ámbito propio, igual que cada iteración de un worker.</summary>
    public async Task<T> UseAsync<T>(Func<CircuitBreakerService, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CircuitBreakerService>());
    }

    public async Task UseAsync(Func<CircuitBreakerService, Task> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<CircuitBreakerService>());
    }

    /// <summary>Ejecuta algo directamente contra el almacén.</summary>
    public async Task<T> UseStoreAsync<T>(Func<ICircuitBreakerStore, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICircuitBreakerStore>());
    }

    public Task<CircuitSnapshot> GetAsync(string name = CircuitNames.Sap) => UseAsync(service => service.GetAsync(name, CancellationToken.None));

    public Task FailAsync(int times, TimeSpan spacing, string code = "sap.unreachable") =>
        UseAsync(async service =>
        {
            for (var n = 0; n < times; n++)
            {
                await service.ReportTransientFailureAsync(CircuitNames.Sap, code, "Sin conexión.", CancellationToken.None);
                Time.Advance(spacing);
            }
        });

    public Task<bool> ProbeAsync(Func<CancellationToken, Task<CircuitProbeResult>> probe) =>
        UseAsync(service => service.RunProbeIfDueAsync(CircuitNames.Sap, probe, CancellationToken.None));

    /// <summary>Abre el circuito con cinco fallas seguidas.</summary>
    public async Task OpenAsync()
    {
        await FailAsync(5, TimeSpan.FromSeconds(1));
        (await GetAsync()).State.ShouldBe(CircuitState.Open);
    }

    public void Dispose() => _provider.Dispose();
}
