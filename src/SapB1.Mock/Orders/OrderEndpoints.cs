using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;
using SapB1.Mock.Items;
using SapB1.Mock.OData;
using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Orders;

internal static class OrderEndpoints
{
    private const string EntitySet = "Orders";

    public static void MapOrders(this RouteGroupBuilder securedServiceLayer)
    {
        securedServiceLayer.MapGet(
            $"/{EntitySet}",
            (HttpContext http, IOrderStore store, IOptions<MockOptions> options) =>
                ODataResults.Collection(
                    http,
                    EntitySet,
                    [.. store.GetAll().Select(SapJson.ToNode)],
                    options.Value.DefaultPageSize));

        securedServiceLayer.MapGet(
            $"/{EntitySet}({{docEntry:int}})",
            (HttpContext http, int docEntry, IOrderStore store) =>
                store.Find(docEntry) is { } order
                    ? ODataResults.Entity(http, EntitySet, SapJson.ToNode(order))
                    : SapResults.NotFound());

        securedServiceLayer.MapPost($"/{EntitySet}", CreateAsync);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        OrderBuilder builder,
        IOrderStore orders,
        IItemStore items,
        CancellationToken cancellationToken)
    {
        var (body, failure) = await SapRequestBody.ReadObjectAsync<Order>(http, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var (input, conversionFailure) = SapRequestBody.ToEntity<Order>(body!);
        if (conversionFailure is not null)
        {
            return conversionFailure;
        }

        var (draft, error) = builder.Build(input!);
        if (error is not null)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.Validation, error);
        }

        var created = orders.Add(draft!);

        // Una orden de venta compromete stock (Committed); el stock físico (InStock) baja recién con la entrega.
        foreach (var line in created.DocumentLines)
        {
            items.Commit(line.ItemCode, line.WarehouseCode!, line.Quantity!.Value);
        }

        return ODataResults.Entity(http, EntitySet, SapJson.ToNode(created), StatusCodes.Status201Created);
    }
}
