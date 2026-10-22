using SapB1.Mock.Admin;

namespace SapB1.Mock.Items;

internal sealed class InMemoryItemStore : IItemStore, IResettable
{
    private readonly Lock _gate = new();
    private Dictionary<string, Item> _items = Seed();

    public IReadOnlyList<Item> GetAll()
    {
        lock (_gate)
        {
            return [.. _items.Values];
        }
    }

    public Item? Find(string itemCode)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(itemCode);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _items = Seed();
        }
    }

    private static Dictionary<string, Item> Seed() =>
        ItemSeed.Create().ToDictionary(item => item.ItemCode, StringComparer.Ordinal);
}
