using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Application.Sap;

namespace ErpSyncHub.Infrastructure.Sap;

/// <summary>
/// La prueba liviana del circuito semiabierto: inicia sesión en SAP y lee un artículo. Va directo al adaptador, sin pasar por el
/// circuit breaker (que a esta altura está abierto y la rechazaría).
/// </summary>
internal sealed class SapCircuitProbe(SapGateway sap) : ISapProbe
{
    public async Task<CircuitProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var result = await sap.GetInventoryPageAsync(changedSince: null, afterItemCode: null, pageSize: 1, cancellationToken);

        if (result.IsSuccess)
        {
            return CircuitProbeResult.Success;
        }

        var error = result.Error!;
        return CircuitProbeResult.Failure(error.Code, error.Message, credentialsRejected: error.Code == SapErrors.InvalidCredentials);
    }
}
