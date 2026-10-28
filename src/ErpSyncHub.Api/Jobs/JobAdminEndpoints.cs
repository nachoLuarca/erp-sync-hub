using System.Globalization;
using System.Security.Claims;
using ErpSyncHub.Api.Admin;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Api.Jobs;

/// <summary>Panel de jobs: listar, filtrar y ver el detalle con sus intentos y errores.</summary>
internal static class JobAdminEndpoints
{
    public static void MapJobAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListJobs")
            .WithSummary("Lista los jobs, del más reciente al más antiguo, con filtros.")
            .WithDescription(
                "Filtros: status (repetible o separado por comas: pending, processing, retrying, succeeded, failed, dead_letter), type, " +
                "externalId, from (inclusive) y to (exclusive) sobre la fecha de creación en ISO 8601 (sin zona se asume UTC), page y pageSize " +
                "(hasta 100). No incluye el payload de la tienda.")
            .Produces<JobPageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetJob")
            .WithSummary("Detalle de un job con todos sus intentos y errores.")
            .WithDescription(
                "El payload de la tienda contiene datos personales del cliente (nombre, correo, dirección, RUT): solo se devuelve con " +
                "includePayload=true.")
            .Produces<JobDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/{id:guid}/retry", RetryOneAsync)
            .WithName("RetryJob")
            .WithSummary("Reintenta un job a mano.")
            .WithDescription(
                "Deja el job disponible de inmediato con un presupuesto de intentos nuevo; el historial de intentos y el último error se " +
                "conservan. Sirve para un job fallido (después de corregir el dato), en el dead letter o esperando su reintento. Responde " +
                "409 si el job está en proceso o ya terminó con éxito.")
            .Produces<JobSummaryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);

        group.MapPost("/retry", RetryManyAsync)
            .WithName("RetryJobs")
            .WithSummary("Reintenta varios jobs a mano.")
            .WithDescription(
                "Recibe hasta 100 ids. Responde 200 con un resultado por id: que uno no se pueda reintentar no impide reintentar a los " +
                "demás.")
            .Produces<RetryJobsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);
    }

    private static async Task<IResult> RetryOneAsync(
        Guid id,
        ClaimsPrincipal user,
        IJobAdmin jobs,
        AdminAuditor auditor,
        CancellationToken cancellationToken)
    {
        var outcome = (await jobs.RetryAsync([id], cancellationToken)).Single();
        await auditor.RecordAsync(user, "job.retry", id.ToString(), OutcomeName(outcome.Result));

        return outcome.Result switch
        {
            JobRetryResult.Retried => Results.Ok(JobSummaryResponse.From(outcome.Job!)),
            JobRetryResult.NotFound => ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, outcome.Message!),
            JobRetryResult.NotRetryable => Conflict("job.not_retryable", outcome.Message!),
            _ => Conflict("job.conflict", outcome.Message!),
        };
    }

    private static async Task<IResult> RetryManyAsync(
        RetryJobsRequest? request,
        ClaimsPrincipal user,
        IJobAdmin jobs,
        AdminAuditor auditor,
        CancellationToken cancellationToken)
    {
        var ids = request?.Ids?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "Indica en ids al menos un job para reintentar.");
        }

        if (ids.Count > RetryJobsRequest.MaxIds)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, $"Se pueden reintentar hasta {RetryJobsRequest.MaxIds} jobs por solicitud.");
        }

        var outcomes = await jobs.RetryAsync(ids, cancellationToken);
        foreach (var outcome in outcomes)
        {
            await auditor.RecordAsync(user, "job.retry", outcome.JobId.ToString(), OutcomeName(outcome.Result));
        }

        return Results.Ok(RetryJobsResponse.From(outcomes));
    }

    private static string OutcomeName(JobRetryResult result) => result switch
    {
        JobRetryResult.Retried => "retried",
        JobRetryResult.NotFound => "not_found",
        JobRetryResult.NotRetryable => "not_retryable",
        _ => "conflict",
    };

    private static IResult Conflict(string code, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: ProblemDetailsSetup.TitleFor(StatusCodes.Status409Conflict),
            type: ProblemDetailsSetup.TypeFor(StatusCodes.Status409Conflict),
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static async Task<IResult> ListAsync(
        string[]? status,
        string? type,
        string? externalId,
        string? from,
        string? to,
        int? page,
        int? pageSize,
        IJobAdmin jobs,
        CancellationToken cancellationToken)
    {
        var statuses = new List<JobStatus>();
        foreach (var name in (status ?? []).SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!JobWireNames.TryParseStatus(name, out var parsed))
            {
                return ProblemDetailsSetup.Problem(
                    StatusCodes.Status400BadRequest,
                    $"El estado '{name}' no existe. Los estados son: {string.Join(", ", JobWireNames.StatusNames)}.");
            }

            statuses.Add(parsed);
        }

        if (!TryParseInstant(from, out var createdFrom))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "from debe ser una fecha en formato ISO 8601, por ejemplo 2026-10-26 o 2026-10-26T15:00:00-03:00.");
        }

        if (!TryParseInstant(to, out var createdTo))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "to debe ser una fecha en formato ISO 8601, por ejemplo 2026-10-27 o 2026-10-27T15:00:00-03:00.");
        }

        var filter = new JobFilter(
            statuses,
            string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim(),
            createdFrom,
            createdTo,
            page ?? 1,
            pageSize ?? JobFilter.DefaultPageSize);

        if (filter.Validate() is { } problem)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, problem);
        }

        return Results.Ok(JobPageResponse.From(await jobs.ListAsync(filter, cancellationToken)));
    }

    private static async Task<IResult> GetAsync(Guid id, bool? includePayload, IJobAdmin jobs, CancellationToken cancellationToken)
    {
        var detail = await jobs.GetAsync(id, includePayload ?? false, cancellationToken);

        return detail is null
            ? ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, $"No existe el job {id}.")
            : Results.Ok(JobDetailResponse.From(detail));
    }

    /// <summary>Una fecha o instante ISO 8601; sin zona horaria se asume UTC. Vacío es válido y significa "sin límite".</summary>
    internal static bool TryParseInstant(string? text, out DateTimeOffset? instant)
    {
        instant = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        instant = parsed;
        return true;
    }
}
