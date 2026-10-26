using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

internal static class AdminServiceCollectionExtensions
{
    /// <summary>Registra la configuración de las claves del panel; se valida al iniciar la aplicación.</summary>
    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminApiKeyOptions>()
            .Bind(configuration.GetSection(AdminApiKeyOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AdminApiKeyOptions>, AdminApiKeyOptionsValidator>());
        services.AddSingleton<AdminApiKeyValidator>();
        return services;
    }

    /// <summary>Protege <c>/admin/*</c> con la API key. Va antes de los endpoints.</summary>
    public static IApplicationBuilder UseAdminAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<AdminApiKeyMiddleware>();

    /// <summary>Deja en el log si el panel está habilitado y con cuántas claves, sin revelar ninguna.</summary>
    public static void ReportAdminAuthentication(this WebApplication app)
    {
        var keys = app.Services.GetRequiredService<AdminApiKeyValidator>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ErpSyncHub.Api.Admin");

        if (keys.IsConfigured)
        {
            AdminLog.Enabled(logger, keys.KeyCount);
        }
        else
        {
            AdminLog.Disabled(logger);
        }
    }
}

internal static partial class AdminLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Panel de administración (/admin/*) habilitado con {KeyCount} clave(s) de acceso")]
    public static partial void Enabled(ILogger logger, int keyCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Panel de administración (/admin/*) DESHABILITADO: no hay ninguna clave de acceso. Define Admin__ApiKey (en Compose, ERPSYNC_ADMIN_API_KEY) para habilitarlo")]
    public static partial void Disabled(ILogger logger);
}
