extern alias SapMock;

using Microsoft.AspNetCore.Mvc.Testing;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>El SAP simulado (SapB1.Mock) corriendo en memoria, al que se conecta el hub en las pruebas.</summary>
internal sealed class SapMockHost : IDisposable
{
    private readonly WebApplicationFactory<SapMock::Program> _factory = new();

    /// <summary>Manejador HTTP que envía las peticiones directamente al mock, sin red.</summary>
    public HttpMessageHandler CreateHandler() => _factory.Server.CreateHandler();

    /// <summary>Hace que el mock dé por expiradas todas las sesiones abiertas.</summary>
    public async Task ExpireSessionsAsync()
    {
        using var admin = _factory.CreateClient();
        (await admin.PostAsync("/_mock/sessions/expire", content: null)).EnsureSuccessStatusCode();
    }

    /// <summary>Restaura los datos semilla del mock.</summary>
    public async Task ResetAsync()
    {
        using var admin = _factory.CreateClient();
        (await admin.PostAsync("/_mock/reset", content: null)).EnsureSuccessStatusCode();
    }

    public void Dispose() => _factory.Dispose();
}
