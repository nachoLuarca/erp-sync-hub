using System.Text.Json;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Api.Jobs;

/// <summary>Los nombres con que el panel expone los estados y resultados: <c>snake_case</c>, igual que en la base de datos.</summary>
internal static class JobWireNames
{
    private static readonly JobStatus[] Lifecycle =
    [
        JobStatus.Pending,
        JobStatus.Processing,
        JobStatus.Retrying,
        JobStatus.Succeeded,
        JobStatus.Failed,
        JobStatus.DeadLetter,
    ];

    private static readonly Dictionary<string, JobStatus> StatusesByName =
        Lifecycle.ToDictionary(ToWire, StringComparer.OrdinalIgnoreCase);

    /// <summary>Los estados válidos para filtrar, en el orden del ciclo de vida.</summary>
    public static IReadOnlyList<string> StatusNames { get; } = [.. Lifecycle.Select(ToWire)];

    public static string ToWire<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static bool TryParseStatus(string name, out JobStatus status) => StatusesByName.TryGetValue(name.Trim(), out status);
}

internal sealed record JobErrorResponse(string Code, string Message);

internal sealed record JobSapResponse(int? DocEntry, int? DocNum, string? CardCode, decimal? Total);

/// <summary>Un job tal como lo lista el panel.</summary>
internal sealed record JobSummaryResponse(
    Guid Id,
    string Source,
    string ExternalId,
    string Type,
    string Status,
    int AttemptCount,
    int CycleAttempts,
    DateTimeOffset? NextAttemptAt,
    JobErrorResponse? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    JobSapResponse? Sap,
    decimal? StoreTotal)
{
    public static JobSummaryResponse From(JobSummary job) =>
        new(
            job.Id,
            job.Source,
            job.ExternalId,
            job.Type,
            JobWireNames.ToWire(job.Status),
            job.AttemptCount,
            job.CycleAttempts,
            job.NextAttemptAt,
            job.LastErrorCode is null ? null : new JobErrorResponse(job.LastErrorCode, job.LastErrorMessage ?? string.Empty),
            job.CreatedAt,
            job.UpdatedAt,
            job.FinishedAt,
            job.SapDocEntry is null ? null : new JobSapResponse(job.SapDocEntry, job.SapDocNum, job.SapCardCode, job.SapTotal),
            job.StoreTotal);
}

internal sealed record JobPageResponse(IReadOnlyList<JobSummaryResponse> Items, int Page, int PageSize, int TotalItems, int TotalPages)
{
    public static JobPageResponse From(JobPage page) =>
        new([.. page.Items.Select(JobSummaryResponse.From)], page.Page, page.PageSize, page.TotalItems, page.TotalPages);
}

internal sealed record JobAttemptResponse(
    int Number,
    string WorkerId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationSeconds,
    string Outcome,
    string? ErrorKind,
    JobErrorResponse? Error)
{
    public static JobAttemptResponse From(JobAttemptInfo attempt) =>
        new(
            attempt.Number,
            attempt.WorkerId,
            attempt.StartedAt,
            attempt.FinishedAt,
            attempt.FinishedAt is { } finished ? Math.Round((finished - attempt.StartedAt).TotalSeconds, 3) : null,
            JobWireNames.ToWire(attempt.Outcome),
            attempt.ErrorKind is { } kind ? JobWireNames.ToWire(kind) : null,
            attempt.ErrorCode is null ? null : new JobErrorResponse(attempt.ErrorCode, attempt.ErrorMessage ?? string.Empty));
}

/// <summary>El detalle de un job: sus datos, sus intentos y, solo si se pidió, el payload de la tienda.</summary>
internal sealed record JobDetailResponse(
    JobSummaryResponse Job,
    IReadOnlyList<JobAttemptResponse> Attempts,
    JsonElement? Payload)
{
    public static JobDetailResponse From(JobDetail detail)
    {
        JsonElement? payload = null;
        if (detail.Payload is not null)
        {
            using var document = JsonDocument.Parse(detail.Payload);
            payload = document.RootElement.Clone();
        }

        return new JobDetailResponse(JobSummaryResponse.From(detail.Job), [.. detail.Attempts.Select(JobAttemptResponse.From)], payload);
    }
}
