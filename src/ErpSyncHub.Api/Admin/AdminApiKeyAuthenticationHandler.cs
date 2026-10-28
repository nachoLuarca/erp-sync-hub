using System.Security.Claims;
using System.Text.Encodings.Web;
using ErpSyncHub.Api.Problems;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

/// <summary>Nombres de los esquemas, políticas y claims del panel.</summary>
internal static class AdminAccess
{
    /// <summary>Esquema de autenticación por API key.</summary>
    public const string Scheme = "AdminApiKey";

    /// <summary>Consultar: jobs, detalle, métricas, mapeos y auditoría.</summary>
    public const string ReadPolicy = "AdminRead";

    /// <summary>Operar: reintentar, sincronizar, cerrar el circuito y eliminar mapeos. Incluye leer.</summary>
    public const string OperatePolicy = "AdminOperate";

    /// <summary>Claim con la huella de la clave presentada.</summary>
    public const string KeyFingerprintClaim = "admin_key_fingerprint";

    public const string ReadRole = "read";
    public const string OperateRole = "operate";

    public static string RoleName(AdminRole role) => role == AdminRole.Operate ? OperateRole : ReadRole;
}

/// <summary>
/// Autentica las peticiones al panel por la cabecera <c>X-Api-Key</c>. Una cabecera repetida es ambigua y se rechaza en vez de
/// elegir una. Si no hay ninguna clave configurada, el desafío responde 503 en lugar de dejar el panel abierto. Las claves
/// presentadas nunca se registran, ni siquiera parciales.
/// </summary>
internal sealed class AdminApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AdminKeyRing keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName];
        if (!keys.IsConfigured || presented.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var caller = presented.Count == 1 ? keys.Authenticate(presented[0]) : null;
        if (caller is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Clave inválida."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, $"clave:{caller.Fingerprint}"),
                new Claim(ClaimTypes.Role, AdminAccess.RoleName(caller.Role)),
                new Claim(AdminAccess.KeyFingerprintClaim, caller.Fingerprint),
            ],
            Scheme.Name);

        // Operar incluye leer.
        if (caller.Role == AdminRole.Operate)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, AdminAccess.ReadRole));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!keys.IsConfigured)
        {
            await ProblemDetailsSetup.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "El panel está deshabilitado porque no hay ninguna clave de acceso configurada (variables de entorno Admin__ReadApiKeys " +
                "o Admin__OperateApiKeys; en Compose, ERPSYNC_ADMIN_READ_API_KEYS o ERPSYNC_ADMIN_OPERATE_API_KEYS).")
                .ExecuteAsync(Context);
            return;
        }

        AdminLog.Rejected(Logger, Request.Method, Request.Path, Context.Connection.RemoteIpAddress?.ToString(), Request.Headers[HeaderName].Count == 0 ? "sin clave" : "clave inválida");
        Response.Headers.WWWAuthenticate = $"ApiKey realm=\"admin\", header=\"{HeaderName}\"";
        await ProblemDetailsSetup.Problem(
            StatusCodes.Status401Unauthorized,
            $"Falta una clave válida en la cabecera {HeaderName}.")
            .ExecuteAsync(Context);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var fingerprint = Context.User.FindFirstValue(AdminAccess.KeyFingerprintClaim);
        AdminLog.Forbidden(Logger, Request.Method, Request.Path, fingerprint);
        await ProblemDetailsSetup.Problem(
            StatusCodes.Status403Forbidden,
            "La clave presentada es de solo lectura: esta acción exige una clave de operación.")
            .ExecuteAsync(Context);
    }
}
