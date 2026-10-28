using System.Text.Json;
using ErpSyncHub.Api.Admin;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Domain.Inventory;

namespace ErpSyncHub.Api.Inventory;

/// <summary>Panel de la sincronización de stock y precios: dispararla a mano y consultar sus ejecuciones.</summary>
internal static class InventorySyncEndpoints
{
    private const int MaxRunsPerPage = 100;
    private const int DefaultRunsPerPage = 20;

    public static void MapInventorySync(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sync/inventory").WithTags("Sincronización de stock y precios");

        group.MapPost(string.Empty, TriggerAsync)
            .WithName("TriggerInventorySync")
            .WithSummary("Dispara una sincronización de stock y precios.")
            .WithDescription(
                "Ejecuta la sincronización ahora y responde cuando termina, con el registro de la ejecución: su estado y cuántos artículos " +
                "se leyeron, actualizaron, no cambiaron, se omitieron o fallaron. Con full=true lee todo el maestro de SAP y envía todo a " +
                "la tienda, ignorando la última foto sincronizada. Responde 200 aunque la ejecución haya terminado con errores (revisa " +
                "el estado), y 409 si ya hay una sincronización en curso o la tienda no está configurada.")
            .Produces<InventorySyncRunResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);

        group.MapGet("/last", GetLastAsync)
            .WithName("GetLastInventorySync")
            .WithSummary("Consulta la última sincronización de stock y precios.")
            .Produces<InventorySyncRunResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/runs", GetRunsAsync)
            .WithName("ListInventorySyncs")
            .WithSummary("Lista las últimas sincronizaciones de stock y precios, de la más reciente a la más antigua.")
            .Produces<IReadOnlyList<InventorySyncRunResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> TriggerAsync(
        bool? full,
        InventorySyncRunner runner,
        IHostApplicationLifetime lifetime)
    {
        // La ejecución solo se interrumpe al detener el hub: si quien llama corta la conexión, la sincronización termina igual.
        var result = await runner.RunAsync(InventorySyncTrigger.Manual, full ?? false, lifetime.ApplicationStopping);

        return result.IsFailure
            ? Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: ProblemDetailsSetup.TitleFor(StatusCodes.Status409Conflict),
                type: ProblemDetailsSetup.TypeFor(StatusCodes.Status409Conflict),
                detail: result.Error!.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code })
            : Results.Ok(InventorySyncRunResponse.From(result.Value));
    }

    private static async Task<IResult> GetLastAsync(IInventorySyncRunStore runs, CancellationToken cancellationToken)
    {
        var last = await runs.GetLastAsync(cancellationToken);

        return last is null
            ? ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, "Todavía no se ha ejecutado ninguna sincronización de stock y precios.")
            : Results.Ok(InventorySyncRunResponse.From(last));
    }

    private static async Task<IResult> GetRunsAsync(int? limit, IInventorySyncRunStore runs, CancellationToken cancellationToken)
    {
        var count = limit ?? DefaultRunsPerPage;
        if (count is < 1 or > MaxRunsPerPage)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, $"limit debe estar entre 1 y {MaxRunsPerPage}.");
        }

        var recent = await runs.GetRecentAsync(count, cancellationToken);
        return Results.Ok(recent.Select(InventorySyncRunResponse.From));
    }
}

/// <summary>Una ejecución de la sincronización de stock y precios, tal como la devuelve el panel.</summary>
/// <param name="Id">Identificador de la ejecución; es el <c>InventoryRunId</c> de sus logs en Seq.</param>
/// <param name="Trigger"><c>scheduled</c> (intervalo) o <c>manual</c> (panel).</param>
/// <param name="Status"><c>running</c>, <c>succeeded</c>, <c>completed_with_errors</c>, <c>failed</c> o <c>abandoned</c>.</param>
/// <param name="Full">Si leyó todo el maestro y envió todo, ignorando la última foto.</param>
/// <param name="ChangedSince">Fecha de SAP desde la que leyó; <see langword="null"/> si leyó todo.</param>
/// <param name="StartedAt">Cuándo empezó.</param>
/// <param name="FinishedAt">Cuándo terminó; <see langword="null"/> si sigue en curso.</param>
/// <param name="DurationSeconds">Cuánto duró; <see langword="null"/> si sigue en curso.</param>
/// <param name="Instance">Instancia del hub que la ejecutó.</param>
/// <param name="Items">Qué pasó con los artículos leídos.</param>
/// <param name="Error">Por qué se interrumpió, si falló.</param>
internal sealed record InventorySyncRunResponse(
    Guid Id,
    string Trigger,
    string Status,
    bool Full,
    DateOnly? ChangedSince,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationSeconds,
    string Instance,
    InventorySyncItemsResponse Items,
    InventorySyncErrorResponse? Error)
{
    public static InventorySyncRunResponse From(InventorySyncRun run) =>
        new(
            run.Id,
            SnakeCase(run.Trigger),
            SnakeCase(run.Status),
            run.Full,
            run.ChangedSince,
            run.StartedAt,
            run.FinishedAt,
            run.FinishedAt is { } finished ? Math.Round((finished - run.StartedAt).TotalSeconds, 3) : null,
            run.Instance,
            new InventorySyncItemsResponse(run.ItemsRead, run.ItemsUpdated, run.ItemsUnchanged, run.ItemsSkipped, run.ItemsFailed),
            run.ErrorCode is null ? null : new InventorySyncErrorResponse(run.ErrorCode, run.ErrorMessage ?? string.Empty));

    private static string SnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

/// <summary>Contadores de una ejecución: cada artículo leído cae en exactamente una de las cuatro últimas categorías.</summary>
internal sealed record InventorySyncItemsResponse(int Read, int Updated, int Unchanged, int Skipped, int Failed);

internal sealed record InventorySyncErrorResponse(string Code, string Message);
