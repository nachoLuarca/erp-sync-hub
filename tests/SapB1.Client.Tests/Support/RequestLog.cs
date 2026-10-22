using System.Collections.Concurrent;
using System.Net;

namespace SapB1.Client.Tests.Support;

/// <summary>Una petición HTTP que salió del cliente hacia SAP.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, HttpStatusCode Status, string? Prefer, string? Cookie)
{
    public bool IsLogin => Method == HttpMethod.Post && PathAndQuery.EndsWith("/Login", StringComparison.Ordinal);

    public override string ToString() => $"{Method} {PathAndQuery} -> {(int)Status}";
}

/// <summary>Registro seguro entre hilos de las peticiones HTTP que salen del cliente.</summary>
internal sealed class RequestLog
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public int Logins => _requests.Count(request => request.IsLogin);

    /// <summary>Las peticiones, como texto "MÉTODO ruta -> estado", para comparar secuencias completas.</summary>
    public IReadOnlyList<string> Sequence => [.. _requests.Select(request => request.ToString())];

    public void Add(RecordedRequest request) => _requests.Enqueue(request);
}

/// <summary>Registra cada petición en el punto más cercano a la red, después del handler de sesión.</summary>
internal sealed class RecordingHandler(RequestLog log) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var prefer = request.Headers.TryGetValues("Prefer", out var preferValues) ? string.Join(',', preferValues) : null;
        var cookie = request.Headers.TryGetValues("Cookie", out var cookieValues) ? string.Join(';', cookieValues) : null;

        var response = await base.SendAsync(request, cancellationToken);

        log.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, response.StatusCode, prefer, cookie));
        return response;
    }
}
