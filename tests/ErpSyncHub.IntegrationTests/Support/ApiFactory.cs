using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SapB1.Client;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Levanta la Api real en memoria apuntando a una base de datos de prueba y, si se indica, al SAP simulado.</summary>
internal static class ApiFactory
{
    /// <summary>Secreto de webhook de las pruebas (la Api exige al menos 16 caracteres).</summary>
    public const string WebhookSecret = "secreto-de-pruebas-del-webhook-0123456789";

    public static WebApplicationFactory<Program> Create(
        string connectionString,
        bool migrateOnStartup = false,
        string? webhookSecret = WebhookSecret,
        SapMockHost? sap = null,
        Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
            builder.UseSetting("Webhooks:WooCommerce:Secret", webhookSecret ?? string.Empty);

            // Credenciales ficticias del SAP simulado; sin mock, apunta a una dirección que nadie atiende.
            builder.UseSetting("SAP:BaseUrl", "http://localhost:9");
            builder.UseSetting("SAP:CompanyDb", SapCredentials.CompanyDb);
            builder.UseSetting("SAP:UserName", SapCredentials.UserName);
            builder.UseSetting("SAP:Password", SapCredentials.Password);

            if (sap is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient(nameof(ISapB1Client)).ConfigurePrimaryHttpMessageHandler(sap.CreateHandler));
            }

            configure?.Invoke(builder);
        });
}

/// <summary>Credenciales del SAP simulado (son las de su appsettings.json y no protegen nada).</summary>
internal static class SapCredentials
{
    public const string CompanyDb = "SBODemoCL";
    public const string UserName = "manager";
    public const string Password = "manager";
}
