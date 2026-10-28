using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SapB1.Client;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Levanta la Api real en memoria apuntando a una base de datos de prueba y, si se indica, a un SAP.</summary>
internal static class ApiFactory
{
    /// <summary>Secreto de webhook de las pruebas (la Api exige al menos 16 caracteres).</summary>
    public const string WebhookSecret = "secreto-de-pruebas-del-webhook-0123456789";

    /// <summary>Clave del panel de las pruebas (la Api exige al menos 32 caracteres).</summary>
    /// <summary>Clave de operación de las pruebas: puede todo lo del panel.</summary>
    public const string AdminApiKey = "clave-de-administracion-de-pruebas-0123456789";

    /// <summary>Clave de lectura de las pruebas: consulta, pero no ejecuta acciones.</summary>
    public const string ReadApiKey = "clave-de-lectura-del-panel-de-pruebas-0123";

    /// <param name="connectionString">Base de datos de la prueba.</param>
    /// <param name="migrateOnStartup">Aplica las migraciones al iniciar.</param>
    /// <param name="webhookSecret">Secreto del webhook; <see langword="null"/> lo deja sin configurar.</param>
    /// <param name="sap">SAP simulado en memoria al que se conecta el hub.</param>
    /// <param name="sapHandler">Alternativa a <paramref name="sap"/>: un servidor falso para provocar fallos de red.</param>
    /// <param name="workerEnabled">
    /// Activa el worker. Por defecto está apagado para que los tests de la Api no vean cambiar sus jobs; cuando se activa
    /// consulta cada 50 ms para que las pruebas sean rápidas.
    /// </param>
    /// <param name="configure">Ajustes adicionales del host.</param>
    /// <param name="adminApiKey">Clave(s) de operación del panel, separadas por coma; <see langword="null"/> no configura ninguna.</param>
    /// <param name="readApiKey">Clave(s) de lectura del panel, separadas por coma; <see langword="null"/> no configura ninguna.</param>
    public static WebApplicationFactory<Program> Create(
        string connectionString,
        bool migrateOnStartup = false,
        string? webhookSecret = WebhookSecret,
        SapMockHost? sap = null,
        Func<HttpMessageHandler>? sapHandler = null,
        bool workerEnabled = false,
        Action<IWebHostBuilder>? configure = null,
        string? adminApiKey = AdminApiKey,
        string? readApiKey = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
            builder.UseSetting("Webhooks:WooCommerce:Secret", webhookSecret ?? string.Empty);
            builder.UseSetting("Admin:OperateApiKeys", adminApiKey ?? string.Empty);
            builder.UseSetting("Admin:ReadApiKeys", readApiKey ?? string.Empty);

            // Credenciales ficticias del SAP simulado; sin mock, apunta a una dirección que nadie atiende.
            builder.UseSetting("SAP:BaseUrl", "http://localhost:9");
            builder.UseSetting("SAP:CompanyDb", SapCredentials.CompanyDb);
            builder.UseSetting("SAP:UserName", SapCredentials.UserName);
            builder.UseSetting("SAP:Password", SapCredentials.Password);

            // La sincronización de stock y precios solo corre en los tests que la piden (ver InventorySyncHost).
            builder.UseSetting("Sync:Inventory:Enabled", "false");

            builder.UseSetting("Worker:Enabled", workerEnabled ? "true" : "false");
            builder.UseSetting("Worker:PollInterval", "00:00:00.050");
            builder.UseSetting("Worker:LeaseDuration", "00:01:00");

            var handler = sap is not null ? sap.CreateHandler : sapHandler;
            if (handler is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient(nameof(ISapB1Client)).ConfigurePrimaryHttpMessageHandler(handler));
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
