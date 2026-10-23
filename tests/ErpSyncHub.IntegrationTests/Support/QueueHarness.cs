using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// La cola de jobs tal como la usa la aplicación (obtenida de la inyección de dependencias), con un reloj controlable.
/// Cada operación usa un ámbito propio, igual que cada worker.
/// </summary>
internal sealed class QueueHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    private QueueHarness(ServiceProvider provider, FakeTimeProvider time)
    {
        _provider = provider;
        Time = time;
    }

    public FakeTimeProvider Time { get; }

    public static QueueHarness For(string connectionString)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(time);

        return new QueueHarness(services.BuildServiceProvider(), time);
    }

    public SyncJob NewJob(string externalId, decimal? storeTotal = null, string payload = "{}") =>
        SyncJob.Create(SyncSources.WooCommerce, externalId, SyncJobTypes.Order, payload, storeTotal, Time.GetUtcNow()).Value;

    public Task<EnqueueResult> EnqueueAsync(SyncJob job) => UseAsync(queue => queue.EnqueueAsync(job, CancellationToken.None));

    public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string workerId, int maxJobs = 1, TimeSpan? lease = null) =>
        UseAsync(queue => queue.ClaimAsync(workerId, maxJobs, lease ?? TimeSpan.FromMinutes(5), CancellationToken.None));

    public Task<Result> CompleteAsync(ClaimedJob claim, SapOrderReference order) =>
        UseAsync(queue => queue.CompleteAsync(claim, order, CancellationToken.None));

    public Task<Result> FailAsync(ClaimedJob claim, ResultError error) =>
        UseAsync(queue => queue.FailAsync(claim, error, CancellationToken.None));

    public void Dispose() => _provider.Dispose();

    private async Task<T> UseAsync<T>(Func<IJobQueue, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IJobQueue>());
    }
}
