using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace SapB1.Client.Tests.Support;

/// <summary>
/// Arma el cliente real con inyección de dependencias, apuntando al mock en memoria
/// (o a un servidor falso), y registra todas las peticiones HTTP que salen hacia SAP.
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    public const string CompanyDb = "SBODemoCL";
    public const string UserName = "manager";
    public const string Password = "manager";

    private readonly WebApplicationFactory<Program>? _mock;
    private readonly ServiceProvider _provider;

    private ClientHarness(WebApplicationFactory<Program>? mock, ServiceProvider provider, RequestLog log)
    {
        _mock = mock;
        _provider = provider;
        Log = log;
    }

    public RequestLog Log { get; }

    public ISapB1Client Client => _provider.GetRequiredService<ISapB1Client>();

    /// <summary>Cliente contra el mock real de SAP, levantado en memoria.</summary>
    public static ClientHarness AgainstMock(Action<SapB1Options>? configure = null)
    {
        var mock = new WebApplicationFactory<Program>();
        return Build(mock, () => mock.Server.CreateHandler(), configure);
    }

    /// <summary>Cliente contra un servidor falso, para simular respuestas que el mock no produce.</summary>
    public static ClientHarness AgainstStub(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<SapB1Options>? configure = null) =>
        Build(null, () => new StubHandler(respond), configure);

    /// <summary>Hace que el mock dé por expiradas todas las sesiones abiertas.</summary>
    public async Task ExpireSessionsAsync()
    {
        using var admin = _mock!.CreateClient();
        (await admin.PostAsync("/_mock/sessions/expire", content: null)).EnsureSuccessStatusCode();
    }

    /// <summary>Restaura los datos semilla del mock.</summary>
    public async Task ResetMockAsync()
    {
        using var admin = _mock!.CreateClient();
        (await admin.PostAsync("/_mock/reset", content: null)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _mock?.Dispose();
    }

    private static ClientHarness Build(
        WebApplicationFactory<Program>? mock,
        Func<HttpMessageHandler> primaryHandler,
        Action<SapB1Options>? configure)
    {
        var log = new RequestLog();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddSapB1Client(options =>
            {
                options.BaseUrl = "http://localhost";
                options.CompanyDb = CompanyDb;
                options.UserName = UserName;
                options.Password = Password;
                configure?.Invoke(options);
            })
            .ConfigurePrimaryHttpMessageHandler(primaryHandler)
            .AddHttpMessageHandler(() => new RecordingHandler(log));

        return new ClientHarness(mock, services.BuildServiceProvider(), log);
    }
}

/// <summary>Servidor HTTP falso que responde con lo que indique el test.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
