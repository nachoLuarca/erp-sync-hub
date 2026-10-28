using System.Security.Claims;
using ErpSyncHub.Api.Admin;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Domain.Resilience;

namespace ErpSyncHub.Api.Resilience;

/// <summary>Panel del circuit breaker: ver su estado y cerrarlo a mano.</summary>
internal static class CircuitBreakerEndpoints
{
    public static void MapCircuitBreakers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/circuit-breakers").WithTags("Circuit breaker");

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListCircuitBreakers")
            .WithSummary("Lista el estado de los circuit breakers.")
            .WithDescription(
                "state es closed (todo normal), open (no se envía trabajo al sistema) o half_open (hay una prueba en curso). Abierto, " +
                "nextProbeAt es cuándo toca la próxima prueba y reason por qué se abrió (transient_failures o credentials_rejected).")
            .Produces<IReadOnlyList<CircuitBreakerResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/{name}/close", CloseAsync)
            .WithName("CloseCircuitBreaker")
            .WithSummary("Cierra un circuit breaker a mano.")
            .WithDescription(
                "Reanuda la cola de inmediato, sin esperar la próxima prueba: úsalo después de corregir la causa (por ejemplo, las " +
                "credenciales de SAP). Si SAP sigue fallando, volverá a abrirse con las siguientes fallas. Responde 200 con el estado " +
                "resultante, también si ya estaba cerrado, y 404 si no existe. Queda en la auditoría.")
            .Produces<CircuitBreakerResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);
    }

    private static async Task<IResult> ListAsync(CircuitBreakerService circuit, CancellationToken cancellationToken) =>
        Results.Ok(await ReadAllAsync(circuit, cancellationToken));

    /// <summary>Todos los circuitos del hub; los que todavía no tienen fila se muestran cerrados.</summary>
    internal static async Task<IReadOnlyList<CircuitBreakerResponse>> ReadAllAsync(CircuitBreakerService circuit, CancellationToken cancellationToken)
    {
        var sap = await circuit.GetAsync(CircuitNames.Sap, cancellationToken);
        return [CircuitBreakerResponse.From(sap)];
    }

    private static async Task<IResult> CloseAsync(
        string name,
        ClaimsPrincipal user,
        CircuitBreakerService circuit,
        AdminAuditor auditor,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(name, CircuitNames.Sap, StringComparison.Ordinal))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, $"No existe el circuit breaker '{name}'. Los circuitos son: {CircuitNames.Sap}.");
        }

        var before = await circuit.GetAsync(name, cancellationToken);
        var after = await circuit.CloseAsync(name, cancellationToken);
        await auditor.RecordAsync(user, "circuit.close", name, before.AllowsWork ? "already_closed" : "closed");

        return Results.Ok(CircuitBreakerResponse.From(after));
    }
}

/// <summary>El estado de un circuit breaker, tal como lo devuelve el panel.</summary>
/// <param name="Name">El sistema que protege, por ejemplo <c>sap</c>.</param>
/// <param name="State"><c>closed</c>, <c>open</c> o <c>half_open</c>.</param>
/// <param name="Reason"><c>transient_failures</c> o <c>credentials_rejected</c>, cuando no está cerrado.</param>
/// <param name="OpenedAt">Cuándo se abrió.</param>
/// <param name="OpenCount">Veces seguidas que se abrió sin cerrarse (define el enfriamiento).</param>
/// <param name="NextProbeAt">Cuándo toca la próxima prueba.</param>
/// <param name="ConsecutiveFailures">Fallas pasajeras consecutivas con el circuito cerrado.</param>
/// <param name="LastErrorCode">Código del último error que cuenta.</param>
/// <param name="LastErrorMessage">Mensaje del último error, sin datos personales.</param>
/// <param name="UpdatedAt">Última modificación.</param>
internal sealed record CircuitBreakerResponse(
    string Name,
    string State,
    string? Reason,
    DateTimeOffset? OpenedAt,
    int OpenCount,
    DateTimeOffset? NextProbeAt,
    int ConsecutiveFailures,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt)
{
    public static CircuitBreakerResponse From(CircuitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new CircuitBreakerResponse(
            snapshot.Name,
            StateName(snapshot.State),
            snapshot.Reason is { } reason ? ReasonName(reason) : null,
            snapshot.OpenedAt,
            snapshot.OpenCount,
            snapshot.NextProbeAt,
            snapshot.ConsecutiveFailures,
            snapshot.LastErrorCode,
            snapshot.LastErrorMessage,
            snapshot.UpdatedAt);
    }

    internal static string StateName(CircuitState state) => state switch
    {
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half_open",
        _ => "closed",
    };

    internal static string ReasonName(CircuitOpenReason reason) =>
        reason == CircuitOpenReason.CredentialsRejected ? "credentials_rejected" : "transient_failures";
}
