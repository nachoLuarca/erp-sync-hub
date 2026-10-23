using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ErpSyncHub.Infrastructure.Health;

/// <summary>
/// Comprueba que PostgreSQL responde y que el esquema está al día. Sin base de datos el hub no puede ni guardar
/// los webhooks que recibe, por eso un fallo es <see cref="HealthStatus.Unhealthy"/>.
/// </summary>
internal sealed class PostgresHealthCheck(ErpSyncHubDbContext context) : IHealthCheck
{
    public const string Name = "postgres";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthCheckContext, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("No se pudo conectar con PostgreSQL.");
            }

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            return pending > 0
                ? HealthCheckResult.Unhealthy($"Faltan {pending} migraciones por aplicar en la base de datos.")
                : HealthCheckResult.Healthy("PostgreSQL responde y el esquema está al día.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // El detalle (host, usuario) no se expone: el mensaje de la excepción puede contener la cadena de conexión.
            return HealthCheckResult.Unhealthy("PostgreSQL no responde.");
        }
    }
}
