using System.Net.Http.Headers;
using System.Text.Json;
using ErpSyncHub.Application.WooCommerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// La Api real en memoria con una base de datos propia, y utilidades para enviarle webhooks firmados y esperar
/// el resultado del worker.
/// </summary>
internal sealed class WebhookHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private WebhookHost(WebApplicationFactory<Program> factory, string connectionString)
    {
        _factory = factory;
        ConnectionString = connectionString;
        Client = factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.AdminApiKey);
    }

    /// <summary>Cliente con la clave del panel puesta en cada petición.</summary>
    public HttpClient Client { get; }

    /// <summary>Un cliente nuevo sin ninguna cabecera, para comprobar lo que ve quien no tiene clave.</summary>
    public HttpClient CreateAnonymousClient() => _factory.CreateClient();

    /// <summary>Los servicios de la Api en ejecución, para tomar una pieza (por ejemplo, el lock de la sincronización).</summary>
    public IServiceProvider Services => _factory.Services;

    public string ConnectionString { get; }

    /// <param name="postgres">PostgreSQL compartido de las pruebas.</param>
    /// <param name="configure">Ajustes adicionales del host.</param>
    /// <param name="sap">SAP simulado al que se conecta el hub.</param>
    /// <param name="sapHandler">Servidor falso de SAP, para provocar fallos de red.</param>
    /// <param name="workerEnabled">Activa el worker que procesa los jobs.</param>
    /// <param name="seed">Prepara la base de datos antes de arrancar la Api (por ejemplo, con jobs que otro worker dejó a medias).</param>
    public static async Task<WebhookHost> StartAsync(
        PostgresFixture postgres,
        Action<IWebHostBuilder>? configure = null,
        SapMockHost? sap = null,
        Func<HttpMessageHandler>? sapHandler = null,
        bool workerEnabled = false,
        Func<string, Task>? seed = null)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        if (seed is not null)
        {
            await seed(connectionString);
        }

        return new WebhookHost(
            ApiFactory.Create(connectionString, sap: sap, sapHandler: sapHandler, workerEnabled: workerEnabled, configure: configure),
            connectionString);
    }

    /// <summary>Arranca la Api sobre una base de datos que ya tiene datos, como un reinicio del servicio.</summary>
    public static WebhookHost StartOnDatabase(
        string connectionString,
        SapMockHost? sap = null,
        bool workerEnabled = true,
        Action<IWebHostBuilder>? configure = null) =>
        new(ApiFactory.Create(connectionString, sap: sap, workerEnabled: workerEnabled, configure: configure), connectionString);

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

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Espera a que la consulta devuelva verdadero. Si no ocurre en el plazo, falla mostrando el estado de los jobs
    /// para que el motivo se vea en el informe de la prueba.
    /// </summary>
    public async Task WaitUntilAsync(string booleanSql, string description, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < limit)
        {
            if (await ScalarAsync<bool>(booleanSql))
            {
                return;
            }

            await Task.Delay(50);
        }

        var state = await ScalarAsync<string>(
            "SELECT coalesce(string_agg(external_id || '=' || status || coalesce('/' || last_error_code, ''), ', '), 'sin jobs') FROM sync_jobs");
        throw new TimeoutException($"Se agotó el tiempo esperando: {description}. Estado de los jobs: {state}.");
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
