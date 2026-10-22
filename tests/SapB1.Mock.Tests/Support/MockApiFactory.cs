using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace SapB1.Mock.Tests.Support;

/// <summary>Levanta el mock en memoria con un reloj controlable para probar la expiración de sesiones.</summary>
public sealed class MockApiFactory : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Cliente que no gestiona cookies: cada test envía la cookie B1SESSION explícitamente,
    /// igual que lo hará SapB1.Client.
    /// </summary>
    public HttpClient CreateSapClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }
}
