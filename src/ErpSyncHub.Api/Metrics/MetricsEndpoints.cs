using ErpSyncHub.Api.Inventory;
using ErpSyncHub.Api.Jobs;
using ErpSyncHub.Api.Resilience;
using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Resilience;

namespace ErpSyncHub.Api.Metrics;

/// <summary>Métricas básicas del panel: el estado de la cola de jobs y de la sincronización de inventario.</summary>
internal static class MetricsEndpoints
{
    public static void MapMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", GetAsync)
            .WithName("GetMetrics")
            .WithTags("Métricas")
            .WithSummary("Jobs por estado, atraso de la cola y última sincronización de inventario.")
            .WithDescription(
                "jobs.byStatus incluye todos los estados, también los que tienen cero. oldestPending es el job pendiente más antiguo: " +
                "una antigüedad que crece indica que los workers no dan abasto o están detenidos. nextRetryAt es cuándo toca el próximo " +
                "reintento automático. circuits trae el estado de los circuit breakers (closed, open o half_open). inventorySync trae la última ejecución, sea cual sea su resultado, y la última sin errores.")
            .Produces<MetricsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> GetAsync(
        IJobAdmin jobs,
        IInventorySyncRunStore inventoryRuns,
        CircuitBreakerService circuit,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var metrics = await jobs.GetMetricsAsync(cancellationToken);
        var last = await inventoryRuns.GetLastAsync(cancellationToken);
        var lastSucceeded = await inventoryRuns.GetLastSucceededAsync(cancellationToken);
        var now = time.GetUtcNow();

        return Results.Ok(new MetricsResponse(
            now,
            new JobMetricsResponse(
                metrics.Total,
                metrics.CountByStatus.ToDictionary(pair => JobWireNames.ToWire(pair.Key), pair => pair.Value),
                metrics.OldestPendingCreatedAt is { } oldest
                    ? new OldestPendingResponse(oldest, Math.Round(Math.Max(0, (now - oldest).TotalSeconds), 3))
                    : null,
                metrics.NextRetryAt),
            new InventoryMetricsResponse(
                last is null ? null : InventorySyncRunResponse.From(last),
                lastSucceeded is null ? null : InventorySyncRunResponse.From(lastSucceeded)),
            await CircuitBreakerEndpoints.ReadAllAsync(circuit, cancellationToken)));
    }
}

internal sealed record MetricsResponse(
    DateTimeOffset GeneratedAt,
    JobMetricsResponse Jobs,
    InventoryMetricsResponse InventorySync,
    IReadOnlyList<CircuitBreakerResponse> Circuits);

internal sealed record JobMetricsResponse(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    OldestPendingResponse? OldestPending,
    DateTimeOffset? NextRetryAt);

internal sealed record OldestPendingResponse(DateTimeOffset CreatedAt, double AgeSeconds);

internal sealed record InventoryMetricsResponse(InventorySyncRunResponse? LastRun, InventorySyncRunResponse? LastSucceededRun);
