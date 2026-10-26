namespace SapB1.Mock.Items;

/// <summary>Cambios al maestro de artículos que en un SAP real harían movimientos de inventario o el usuario en el maestro.</summary>
internal static class ItemAdminEndpoints
{
    public static void MapItemAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPatch("/items/{itemCode}", (string itemCode, ItemAdjustment adjustment, IItemStore store) =>
        {
            if (adjustment.Validate() is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            return store.Adjust(itemCode, adjustment) ? Results.NoContent() : Results.NotFound();
        });
    }
}
