namespace SapB1.Mock.Items;

/// <summary>Persistencia del maestro de artículos del mock.</summary>
internal interface IItemStore
{
    IReadOnlyList<Item> GetAll();

    Item? Find(string itemCode);
}
