using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

internal static class AdminServiceCollectionExtensions
{
    /// <summary>
    /// Registra la autenticación por API key del panel, sus políticas (lectura y operación) y la validación de las claves, que se
    /// comprueba al iniciar la aplicación.
    /// </summary>
    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminApiKeyOptions>()
            .Bind(configuration.GetSection(AdminApiKeyOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AdminApiKeyOptions>, AdminApiKeyOptionsValidator>());
        services.AddSingleton<AdminKeyRing>();
        services.AddScoped<AdminAuditor>();

        // El esquema solo actúa donde un endpoint exige autorización: el webhook, la salud y la documentación no lo usan.
        services.AddAuthentication(AdminAccess.Scheme)
            .AddScheme<AuthenticationSchemeOptions, AdminApiKeyAuthenticationHandler>(AdminAccess.Scheme, configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminAccess.ReadPolicy, policy => policy
                .AddAuthenticationSchemes(AdminAccess.Scheme)
                .RequireRole(AdminAccess.ReadRole))
            .AddPolicy(AdminAccess.OperatePolicy, policy => policy
                .AddAuthenticationSchemes(AdminAccess.Scheme)
                .RequireRole(AdminAccess.OperateRole));
        return services;
    }

    /// <summary>
    /// El grupo <c>/admin</c>: todo lo que cuelga de él exige al menos una clave de lectura, existan o no los endpoints (una ruta
    /// desconocida también responde 401 y no se puede explorar el panel sin clave).
    /// </summary>
    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(AdminAccess.ReadPolicy);
        admin.MapFallback("/{**path}", () => Results.StatusCode(StatusCodes.Status404NotFound)).ExcludeFromDescription();
        return admin;
    }

    /// <summary>Deja en el log si el panel está habilitado y con cuántas claves de cada nivel, sin revelar ninguna.</summary>
    public static void ReportAdminAuthentication(this WebApplication app)
    {
        var keys = app.Services.GetRequiredService<AdminKeyRing>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ErpSyncHub.Api.Admin");

        if (keys.IsConfigured)
        {
            AdminLog.Enabled(logger, keys.ReadKeyCount, keys.OperateKeyCount);
        }
        else
        {
            AdminLog.Disabled(logger);
        }
    }
}

internal static partial class AdminLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Acceso rechazado al panel: {Method} {Path} desde {RemoteIp} ({Reason})")]
    public static partial void Rejected(ILogger logger, string method, PathString path, string? remoteIp, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acción de operación rechazada a una clave de lectura: {Method} {Path} (clave {KeyFingerprint})")]
    public static partial void Forbidden(ILogger logger, string method, PathString path, string? keyFingerprint);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Panel de administración (/admin/*) habilitado con {ReadKeyCount} clave(s) de lectura y {OperateKeyCount} de operación")]
    public static partial void Enabled(ILogger logger, int readKeyCount, int operateKeyCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Panel de administración (/admin/*) DESHABILITADO: no hay ninguna clave de acceso. Define Admin__ReadApiKeys o " +
                  "Admin__OperateApiKeys (en Compose, ERPSYNC_ADMIN_READ_API_KEYS o ERPSYNC_ADMIN_OPERATE_API_KEYS) para habilitarlo")]
    public static partial void Disabled(ILogger logger);
}
