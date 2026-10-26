namespace SapB1.Mock.Items;

/// <summary>Persistencia del maestro de artículos del mock.</summary>
internal interface IItemStore
{
    IReadOnlyList<Item> GetAll();

    Item? Find(string itemCode);

    /// <summary>Aumenta el stock comprometido del artículo en el almacén. Devuelve <see langword="false"/> si no existe.</summary>
    bool Commit(string itemCode, string warehouseCode, decimal quantity);

    /// <summary>Aplica un cambio manual y actualiza la fecha de modificación. Devuelve <see langword="false"/> si el artículo no existe.</summary>
    bool Adjust(string itemCode, ItemAdjustment adjustment);
}
