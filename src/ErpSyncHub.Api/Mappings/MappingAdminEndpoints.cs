using System.Security.Claims;
using ErpSyncHub.Api.Admin;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Sync;

namespace ErpSyncHub.Api.Mappings;

/// <summary>Panel de mapeos: listar y filtrar los de producto y cliente, y eliminar uno obsoleto.</summary>
internal static class MappingAdminEndpoints
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public static void MapMappingAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mappings").WithTags("Mapeos");

        group.MapGet("/products", ListProductsAsync)
            .WithName("ListProductMappings")
            .WithSummary("Lista los mapeos SKU ↔ ItemCode, ordenados por SKU.")
            .WithDescription("Filtros: sku e itemCode (contienen el texto, sin distinguir mayúsculas), page y pageSize (hasta 100).")
            .Produces<MappingPageResponse<ProductMappingResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/customers", ListCustomersAsync)
            .WithName("ListCustomerMappings")
            .WithSummary("Lista los mapeos cliente ↔ CardCode, del más reciente al más antiguo.")
            .WithDescription(
                "La clave de un invitado es su correo y el CardCode de un cliente nuevo contiene su RUT: por defecto se devuelven ocultos " +
                "(email:[correo], C[RUT]). Con includePersonalData=true, o para buscar por key o cardCode exactos, hace falta una clave de " +
                "operación. Filtros: kind (registered o guest), key, cardCode, page y pageSize (hasta 100).")
            .Produces<MappingPageResponse<CustomerMappingResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/products", DeleteProductAsync)
            .WithName("DeleteProductMapping")
            .WithSummary("Elimina el mapeo de un SKU.")
            .WithDescription(
                "El mapeo se vuelve a resolver solo la próxima vez que llegue un pedido con ese SKU (el SKU de la tienda se busca como " +
                "ItemCode en SAP). Responde 204, o 404 si no había mapeo. Queda en la auditoría.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);

        group.MapDelete("/customers", DeleteCustomerAsync)
            .WithName("DeleteCustomerMapping")
            .WithSummary("Elimina el mapeo de un cliente.")
            .WithDescription(
                "key es la clave del cliente (customer:4521, o email:... para un invitado). El cliente se vuelve a resolver en SAP la " +
                "próxima vez que compre. Responde 204, o 404 si no había mapeo. Queda en la auditoría.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AdminAccess.OperatePolicy);
    }

    private static async Task<IResult> ListProductsAsync(
        string? sku,
        string? itemCode,
        int? page,
        int? pageSize,
        IMappingAdmin mappings,
        CancellationToken cancellationToken)
    {
        if (Validate(page, pageSize) is { } problem)
        {
            return problem;
        }

        var result = await mappings.ListProductsAsync(
            new ProductMappingFilter(NullIfBlank(sku), NullIfBlank(itemCode), page ?? 1, pageSize ?? DefaultPageSize),
            cancellationToken);

        return Results.Ok(MappingPageResponse<ProductMappingResponse>.From(
            result,
            mapping => new ProductMappingResponse(mapping.Sku, mapping.ItemCode, mapping.CreatedAt),
            personalDataHidden: false));
    }

    private static async Task<IResult> ListCustomersAsync(
        string? kind,
        string? key,
        string? cardCode,
        bool? includePersonalData,
        int? page,
        int? pageSize,
        ClaimsPrincipal user,
        IMappingAdmin mappings,
        CancellationToken cancellationToken)
    {
        if (Validate(page, pageSize) is { } problem)
        {
            return problem;
        }

        CustomerKeyKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (string.Equals(kind.Trim(), "registered", StringComparison.OrdinalIgnoreCase))
            {
                parsedKind = CustomerKeyKind.Registered;
            }
            else if (string.Equals(kind.Trim(), "guest", StringComparison.OrdinalIgnoreCase))
            {
                parsedKind = CustomerKeyKind.Guest;
            }
            else
            {
                return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "kind debe ser registered o guest.");
            }
        }

        // Ver o buscar por datos personales exige operar: una clave de lectura solo ve los mapeos ocultos.
        var wantsPersonalData = includePersonalData == true || !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(cardCode);
        if (wantsPersonalData && !user.IsInRole(AdminAccess.OperateRole))
        {
            return ProblemDetailsSetup.Problem(
                StatusCodes.Status403Forbidden,
                "Ver los datos personales de los mapeos (includePersonalData) o buscar por key o cardCode exige una clave de operación.");
        }

        var result = await mappings.ListCustomersAsync(
            new CustomerMappingFilter(NullIfBlank(key), NullIfBlank(cardCode), parsedKind, page ?? 1, pageSize ?? DefaultPageSize),
            cancellationToken);

        var reveal = includePersonalData == true;
        return Results.Ok(MappingPageResponse<CustomerMappingResponse>.From(
            result,
            mapping => new CustomerMappingResponse(
                reveal ? mapping.Key : Hide(mapping.Key),
                reveal ? mapping.CardCode : Hide(mapping.CardCode),
                mapping.CreatedAt),
            personalDataHidden: !reveal));
    }

    private static async Task<IResult> DeleteProductAsync(
        string? sku,
        ClaimsPrincipal user,
        IMappingStore mappings,
        AdminAuditor auditor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "Indica en sku el SKU cuyo mapeo quieres eliminar.");
        }

        var deleted = await mappings.DeleteProductMappingAsync(sku, cancellationToken);
        await auditor.RecordAsync(user, "mapping.delete_product", sku.Trim(), deleted ? "deleted" : "not_found");

        return deleted
            ? Results.NoContent()
            : ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, $"No hay un mapeo para el SKU '{sku.Trim()}'.");
    }

    private static async Task<IResult> DeleteCustomerAsync(
        string? key,
        ClaimsPrincipal user,
        IMappingStore mappings,
        AdminAuditor auditor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, "Indica en key la clave del cliente cuyo mapeo quieres eliminar.");
        }

        var deleted = await mappings.DeleteCustomerMappingAsync(key, cancellationToken);

        // La clave de un invitado es su correo: la auditoría registra la clave con los datos personales ocultos.
        await auditor.RecordAsync(user, "mapping.delete_customer", Hide(key.Trim()), deleted ? "deleted" : "not_found");

        return deleted
            ? Results.NoContent()
            : ProblemDetailsSetup.Problem(StatusCodes.Status404NotFound, "No hay un mapeo para ese cliente.");
    }

    private static IResult? Validate(int? page, int? pageSize)
    {
        if (page is < 1 || pageSize is < 1 or > MaxPageSize)
        {
            return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, $"page debe ser 1 o más y pageSize estar entre 1 y {MaxPageSize}.");
        }

        return null;
    }

    private static string Hide(string value) => PersonalDataRedactor.Redact(value) ?? value;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Un mapeo de producto.</summary>
internal sealed record ProductMappingResponse(string Sku, string ItemCode, DateTimeOffset CreatedAt);

/// <summary>Un mapeo de cliente; la clave y el CardCode vienen ocultos salvo que se pida includePersonalData con una clave de operación.</summary>
internal sealed record CustomerMappingResponse(string Key, string CardCode, DateTimeOffset CreatedAt);

/// <summary>Una página de mapeos.</summary>
/// <param name="Items">Los mapeos de la página.</param>
/// <param name="Page">Número de página, desde 1.</param>
/// <param name="PageSize">Mapeos por página.</param>
/// <param name="TotalItems">Cantidad de mapeos que cumplen el filtro.</param>
/// <param name="TotalPages">Cantidad de páginas con este tamaño.</param>
/// <param name="PersonalDataHidden">Verdadero si la clave y el CardCode de los clientes están ocultos.</param>
internal sealed record MappingPageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages, bool PersonalDataHidden)
{
    public static MappingPageResponse<T> From<TSource>(MappingPage<TSource> page, Func<TSource, T> map, bool personalDataHidden) =>
        new([.. page.Items.Select(map)], page.Page, page.PageSize, page.TotalItems, page.TotalPages, personalDataHidden);
}
