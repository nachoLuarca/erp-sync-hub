using System.Security.Claims;
using ErpSyncHub.Application.Admin;
using ErpSyncHub.Domain.Admin;

namespace ErpSyncHub.Api.Admin;

/// <summary>
/// Deja constancia de las acciones manuales del panel con la huella de la clave que las hizo (nunca la clave). Registra en la
/// tabla <c>admin_audit</c> y en el log, para verlas también en Seq.
/// </summary>
internal sealed partial class AdminAuditor(IAdminAudit audit, TimeProvider time, ILogger<AdminAuditor> logger)
{
    /// <summary>Tiempo máximo para escribir la auditoría: la acción ya se hizo y no debe perderse el registro si el cliente se desconecta.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <param name="user">Quien hizo la acción (trae la huella y el nivel de su clave).</param>
    /// <param name="action">La acción, por ejemplo <c>job.retry</c>.</param>
    /// <param name="target">Sobre qué, por ejemplo el id del job.</param>
    /// <param name="outcome">Cómo terminó, por ejemplo <c>retried</c> o <c>not_found</c>.</param>
    public async Task RecordAsync(ClaimsPrincipal user, string action, string? target, string outcome)
    {
        ArgumentNullException.ThrowIfNull(user);

        var fingerprint = user.FindFirstValue(AdminAccess.KeyFingerprintClaim) ?? "desconocida";
        var role = user.FindAll(ClaimTypes.Role).Any(claim => claim.Value == AdminAccess.OperateRole) ? AdminAccess.OperateRole : AdminAccess.ReadRole;

        LogAction(action, target, outcome, fingerprint, role);

        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await audit.RecordAsync(AdminAuditEntry.Create(time.GetUtcNow(), action, target, fingerprint, role, outcome), timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // La acción ya ocurrió: no se la oculta a quien la pidió, pero la falta de registro no puede pasar en silencio.
            LogAuditFailed(exception, action, target);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Acción manual {AuditAction} sobre {AuditTarget}: {AuditOutcome} (clave {KeyFingerprint}, nivel {KeyRole})")]
    private partial void LogAction(string auditAction, string? auditTarget, string auditOutcome, string keyFingerprint, string keyRole);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo registrar en la auditoría la acción {AuditAction} sobre {AuditTarget}")]
    private partial void LogAuditFailed(Exception exception, string auditAction, string? auditTarget);
}
