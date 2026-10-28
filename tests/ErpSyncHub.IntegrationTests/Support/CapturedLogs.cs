using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Una línea de log capturada: su nivel y su mensaje ya armado.</summary>
internal sealed record CapturedLog(LogLevel Level, string Message);

/// <summary>Guarda lo que escriben los <see cref="ILogger"/> de un proveedor, para comprobar qué se registró.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();

    public IReadOnlyList<CapturedLog> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new Capture(_records);

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<CapturedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            records.Enqueue(new CapturedLog(logLevel, formatter(state, exception)));
        }
    }
}
