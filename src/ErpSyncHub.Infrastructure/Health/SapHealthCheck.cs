using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SapB1.Client;

namespace ErpSyncHub.Infrastructure.Health;

/// <summary>
/// Comprueba que SAP responde y acepta las credenciales (inicia sesión y lee un artículo). Se registra con estado
/// <see cref="HealthStatus.Degraded"/> en caso de fallo: el hub sigue recibiendo y guardando pedidos mientras SAP está
/// caído, y no tiene sentido que un orquestador lo reinicie por eso.
/// </summary>
/// <remarks>
/// El resultado se conserva unos segundos: las sondas de salud son frecuentes y no deben generar tráfico constante hacia SAP.
/// </remarks>
internal sealed class SapHealthCheck(IServiceScopeFactory scopes, TimeProvider time) : IHealthCheck, IDisposable
{
    public const string Name = "sap";

    /// <summary>Cuánto tiempo se reutiliza el último resultado.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    /// <summary>Tiempo máximo que se espera a SAP en una comprobación.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult _last;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (now - _lastAt < CacheDuration)
            {
                return _last;
            }

            _last = await ProbeAsync(context, cancellationToken);
            _lastAt = time.GetUtcNow();
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<HealthCheckResult> ProbeAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sap = scope.ServiceProvider.GetRequiredService<ISapB1Client>();

            await foreach (var item in sap.Items.QueryAsync(new SapQuery { Top = 1, Select = ["ItemCode"] }, timeout.Token))
            {
                _ = item;
                break;
            }

            return HealthCheckResult.Healthy("SAP responde y acepta las credenciales.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(context, "SAP no respondió dentro del tiempo de espera.");
        }
        catch (SapB1SessionException)
        {
            return Failed(context, "SAP rechazó el inicio de sesión: revise las credenciales (SAP__UserName, SAP__Password, SAP__CompanyDb).");
        }
        catch (SapB1ApiException exception)
        {
            return Failed(context, $"SAP respondió con un error (HTTP {(int)exception.StatusCode}).");
        }
        catch (HttpRequestException)
        {
            return Failed(context, "No se pudo conectar con SAP.");
        }
    }

    private static HealthCheckResult Failed(HealthCheckContext context, string description) =>
        new(context.Registration.FailureStatus, description);
}
