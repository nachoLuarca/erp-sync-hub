using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;
using SapB1.Mock.OData;
using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Items;

internal static class ItemEndpoints
{
    private const string EntitySet = "Items";

    public static void MapItems(this RouteGroupBuilder securedServiceLayer)
    {
        securedServiceLayer.MapGet(
            $"/{EntitySet}",
            (HttpContext http, IItemStore store, IOptions<MockOptions> options) =>
                ODataResults.Collection(
                    http,
                    EntitySet,
                    [.. store.GetAll().Select(SapJson.ToNode)],
                    options.Value.DefaultPageSize));

        securedServiceLayer.MapGet(
            $"/{EntitySet}('{{itemCode}}')",
            (HttpContext http, string itemCode, IItemStore store) =>
                store.Find(ODataKey.Unescape(itemCode)) is { } item
                    ? ODataResults.Entity(http, EntitySet, SapJson.ToNode(item))
                    : SapResults.NotFound());
    }
}
