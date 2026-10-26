extern alias SapMock;

using System.Net.Http.Json;
using System.Text.Json;
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

    /// <summary>Cambia stock, precio o estado de un artículo del mock (<c>PATCH /_mock/items/{itemCode}</c>).</summary>
    public async Task AdjustItemAsync(string itemCode, object changes)
    {
        using var admin = _factory.CreateClient();
        (await admin.PatchAsJsonAsync($"/_mock/items/{itemCode}", changes)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Consulta directa al Service Layer del mock, con su propia sesión: sirve para comprobar qué quedó en SAP
    /// sin pasar por el cliente del hub.
    /// </summary>
    public async Task<JsonDocument> GetAsync(string pathAndQuery)
    {
        using var http = NewClient();
        var session = await LoginAsync(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/b1s/v1/{pathAndQuery}");
        request.Headers.Add("Cookie", $"B1SESSION={session}");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Crea un registro directamente en el mock (por ejemplo, una orden que "ya existía").</summary>
    public async Task PostAsync(string entitySet, object body)
    {
        using var http = NewClient();
        var session = await LoginAsync(http);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/b1s/v1/{entitySet}")
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions()),
        };
        request.Headers.Add("Cookie", $"B1SESSION={session}");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<string> LoginAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync(
            "/b1s/v1/Login",
            new { CompanyDB = SapCredentials.CompanyDb, UserName = SapCredentials.UserName, Password = SapCredentials.Password });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("SessionId").GetString()!;
    }
}
