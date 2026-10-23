using System.Net.Http.Headers;
using System.Text.Json;
using ErpSyncHub.Application.WooCommerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>La Api real en memoria con una base de datos propia, y utilidades para enviarle webhooks firmados.</summary>
internal sealed class WebhookHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private WebhookHost(WebApplicationFactory<Program> factory, string connectionString)
    {
        _factory = factory;
        ConnectionString = connectionString;
        Client = factory.CreateClient();
    }

    public HttpClient Client { get; }

    public string ConnectionString { get; }

    public static async Task<WebhookHost> StartAsync(PostgresFixture postgres, Action<IWebHostBuilder>? configure = null)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        return new WebhookHost(ApiFactory.Create(connectionString, configure: configure), connectionString);
    }

    /// <summary>Arranca la Api contra una base de datos que no existe, para simular una caída de PostgreSQL.</summary>
    public static WebhookHost StartWithUnreachableDatabase() =>
        new(ApiFactory.Create("Host=127.0.0.1;Port=1;Database=inexistente;Username=x;Password=x;Timeout=2;Command Timeout=2"), "n/a");

    /// <summary>Envía el cuerpo con su firma correcta, como lo haría WooCommerce.</summary>
    public Task<HttpResponseMessage> PostSignedAsync(byte[] body, string? topic = "order.updated", string secret = ApiFactory.WebhookSecret) =>
        PostAsync(body, WooCommerceSignature.Compute(secret, body), topic);

    public Task<HttpResponseMessage> PostSignedAsync(string json, string? topic = "order.updated") =>
        PostSignedAsync(System.Text.Encoding.UTF8.GetBytes(json), topic);

    public async Task<HttpResponseMessage> PostAsync(byte[] body, string? signature, string? topic = "order.updated")
    {
        using var content = new ByteArrayContent(body);
        return await PostAsync(content, signature, topic);
    }

    public async Task<HttpResponseMessage> PostAsync(HttpContent content, string? signature, string? topic = "order.updated")
    {
        content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/woocommerce/orders") { Content = content };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(WooCommerceSignature.HeaderName, signature);
        }

        if (topic is not null)
        {
            request.Headers.TryAddWithoutValidation("X-WC-Webhook-Topic", topic);
        }

        request.Headers.TryAddWithoutValidation("X-WC-Webhook-Delivery-ID", Guid.NewGuid().ToString("N"));
        return await Client.SendAsync(request);
    }

    public async Task<long> JobCountAsync() => await ScalarAsync<long>("SELECT count(*) FROM sync_jobs");

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return await reader.IsDBNullAsync(0) ? default : reader.GetFieldValue<T>(0);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>Lee la respuesta JSON.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
