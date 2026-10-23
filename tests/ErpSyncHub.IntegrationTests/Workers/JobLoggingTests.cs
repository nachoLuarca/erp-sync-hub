using System.Collections.Concurrent;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Workers;

/// <summary>Los logs de un job se pueden seguir de principio a fin por su identificador.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class JobLoggingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Todos_los_logs_de_un_job_llevan_su_identificador_como_propiedad()
    {
        var sink = new CollectingSink();
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)),
            sap: sap,
            workerEnabled: true);

        await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"));
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");
        var jobId = await host.ScalarAsync<Guid>("SELECT id FROM sync_jobs");

        // Los eventos del worker son los que están dentro del ámbito del job (traen el número de intento).
        var jobEvents = sink.Events.Where(logEvent => logEvent.Properties.ContainsKey("Attempt")).ToList();
        jobEvents.Count.ShouldBeGreaterThanOrEqualTo(3); // inicio, cliente creado, orden creada, job completado
        jobEvents.ShouldAllBe(logEvent => logEvent.Properties["JobId"].ToString().Trim('"') == jobId.ToString());
        jobEvents.ShouldAllBe(logEvent => logEvent.Properties["ExternalId"].ToString().Trim('"') == "5501");
        jobEvents.Select(logEvent => logEvent.RenderMessage()).ShouldContain(message => message.Contains("Orden 1", StringComparison.Ordinal));
        jobEvents.Select(logEvent => logEvent.RenderMessage()).ShouldContain(message => message.Contains("Job completado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Una_advertencia_de_totales_distintos_queda_asociada_al_job()
    {
        var sink = new CollectingSink();
        using var sap = new SapMockHost();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)),
            sap: sap,
            workerEnabled: true);

        await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"));
        await host.WaitUntilAsync("SELECT bool_and(status = 'succeeded') FROM sync_jobs", "que el job termine");

        var warning = sink.Events.Where(logEvent => logEvent.Level == LogEventLevel.Warning && logEvent.Properties.ContainsKey("Attempt")).ToList().ShouldHaveSingleItem();
        warning.RenderMessage().ShouldContain("difiere");
        warning.Properties["OrderId"].ToString().ShouldBe("5501");
    }

    [Fact]
    public async Task Los_logs_del_webhook_llevan_el_identificador_de_la_entrega()
    {
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        await host.PostSignedAsync(Fixtures.ReadBytes("woocommerce/order-processing.json"));

        sink.Events.ShouldContain(logEvent => logEvent.Properties.ContainsKey("DeliveryId") && logEvent.Properties.ContainsKey("StatusCode"));
    }

    [Fact]
    public async Task Las_sondas_de_health_no_llenan_el_registro()
    {
        var sink = new CollectingSink();
        await using var host = await WebhookHost.StartAsync(
            postgres,
            builder => builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));

        await host.Client.GetAsync("/health");
        await host.Client.GetAsync("/health");

        sink.Events.Where(logEvent => logEvent.RenderMessage().Contains("/health", StringComparison.Ordinal))
            .ShouldAllBe(logEvent => logEvent.Level == LogEventLevel.Verbose);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
