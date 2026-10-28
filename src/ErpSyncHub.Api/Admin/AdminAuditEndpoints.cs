using ErpSyncHub.Api.Jobs;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.Admin;
using ErpSyncHub.Domain.Admin;

namespace ErpSyncHub.Api.Admin;

/// <summary>Consulta de la auditoría de las acciones manuales. Solo lectura: no hay endpoints para modificarla ni borrarla.</summary>
internal static class AdminAuditEndpoints
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public static void MapAdminAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit", ListAsync)
            .WithName("ListAdminAudit")
            .WithTags("Auditoría")
            .WithSummary("Lista las acciones manuales hechas desde el panel, de la más reciente a la más antigua.")
            .WithDescription(
                "Cada entrada trae la acción, sobre qué, cómo terminó y la huella de la clave que la hizo (los primeros caracteres de su " +
                "SHA-256: nunca la clave). Filtros: action, keyFingerprint, from (inclusive) y to (exclusive) en ISO 8601, page y pageSize " +
                "(hasta 100). El registro es de solo inserción.")
            .Produces<AdminAuditPageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> ListAsync(
        string? action,
        string? keyFingerprint,
        string? from,
        string? to,
        int? page,
        int? pageSize,
        IAdminAudit audit,
        CancellationToken cancellationToken)
    {
        if (!JobAdminEndpoints.TryParseInstant(from, out var fromInstant))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "from debe ser una fecha en formato ISO 8601, por ejemplo 2026-10-26.");
        }

        if (!JobAdminEndpoints.TryParseInstant(to, out var toInstant))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "to debe ser una fecha en formato ISO 8601, por ejemplo 2026-10-27.");
        }

        var number = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (number < 1 || size < 1 || size > MaxPageSize)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, $"page debe ser 1 o más y pageSize estar entre 1 y {MaxPageSize}.");
        }

        var result = await audit.ListAsync(
            new AdminAuditFilter(
                string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
                string.IsNullOrWhiteSpace(keyFingerprint) ? null : keyFingerprint.Trim().ToLowerInvariant(),
                fromInstant,
                toInstant,
                number,
                size),
            cancellationToken);

        return Results.Ok(AdminAuditPageResponse.From(result));
    }
}

/// <summary>Una acción manual registrada.</summary>
/// <param name="Id">Identificador de la entrada.</param>
/// <param name="OccurredAt">Cuándo ocurrió.</param>
/// <param name="Action">La acción, por ejemplo <c>job.retry</c>.</param>
/// <param name="Target">Sobre qué se hizo.</param>
/// <param name="KeyFingerprint">Huella de la clave que la hizo.</param>
/// <param name="Role">Nivel de esa clave.</param>
/// <param name="Outcome">Cómo terminó.</param>
internal sealed record AdminAuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    string? Target,
    string KeyFingerprint,
    string Role,
    string Outcome)
{
    public static AdminAuditEntryResponse From(AdminAuditEntry entry) =>
        new(entry.Id, entry.OccurredAt, entry.Action, entry.Target, entry.KeyFingerprint, entry.Role, entry.Outcome);
}

/// <summary>Una página del listado de auditoría.</summary>
internal sealed record AdminAuditPageResponse(IReadOnlyList<AdminAuditEntryResponse> Items, int Page, int PageSize, int TotalItems, int TotalPages)
{
    public static AdminAuditPageResponse From(AdminAuditPage page) =>
        new([.. page.Items.Select(AdminAuditEntryResponse.From)], page.Page, page.PageSize, page.TotalItems, page.TotalPages);
}
