using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Configura la Api de las pruebas para sincronizar stock y precios contra una tienda simulada.</summary>
internal static class InventorySyncHost
{
    /// <param name="store">Tienda WooCommerce simulada a la que se conecta el hub; <see langword="null"/> la deja sin configurar.</param>
    /// <param name="interval">Cada cuánto corre la sincronización periódica.</param>
    /// <param name="sink">Si se indica, recoge los logs.</param>
    /// <param name="settings">Ajustes de configuración adicionales (por ejemplo, <c>Sync:Inventory:Warehouses</c>).</param>
    public static Action<IWebHostBuilder> Configure(
        FakeWooCommerceHandler? store,
        TimeSpan? interval = null,
        CollectingSink? sink = null,
        IReadOnlyDictionary<string, string>? settings = null) =>
        builder =>
        {
            builder.UseSetting("Sync:Inventory:Enabled", "true");
            builder.UseSetting("Sync:Inventory:Interval", (interval ?? TimeSpan.FromMinutes(15)).ToString("c"));

            if (store is not null)
            {
                builder.UseSetting("Store:WooCommerce:BaseUrl", "https://tienda.example.com");
                builder.UseSetting("Store:WooCommerce:ConsumerKey", FakeWooCommerceHandler.ConsumerKey);
                builder.UseSetting("Store:WooCommerce:ConsumerSecret", FakeWooCommerceHandler.ConsumerSecret);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                if (store is not null)
                {
                    services.AddHttpClient("IStoreGateway").ConfigurePrimaryHttpMessageHandler(() => store);
                }

                if (sink is not null)
                {
                    services.AddSingleton<ILogEventSink>(sink);
                }
            });
        };
}
