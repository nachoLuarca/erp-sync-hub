using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Levanta la Api real en memoria apuntando a una base de datos de prueba.</summary>
internal static class ApiFactory
{
    /// <summary>Secreto de webhook de las pruebas (la Api exige al menos 16 caracteres).</summary>
    public const string WebhookSecret = "secreto-de-pruebas-del-webhook-0123456789";

    public static WebApplicationFactory<Program> Create(
        string connectionString,
        bool migrateOnStartup = false,
        string? webhookSecret = WebhookSecret,
        Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
            builder.UseSetting("Webhooks:WooCommerce:Secret", webhookSecret ?? string.Empty);
            configure?.Invoke(builder);
        });
}
