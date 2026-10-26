using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Guarda los eventos de log de Serilog para comprobar qué quedó registrado y con qué propiedades.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
