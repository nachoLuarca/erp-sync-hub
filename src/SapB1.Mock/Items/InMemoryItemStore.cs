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

    public bool Commit(string itemCode, string warehouseCode, decimal quantity)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(itemCode, out var item))
            {
                return false;
            }

            var warehouses = item.ItemWarehouseInfoCollection
                .Select(w => w.WarehouseCode == warehouseCode ? w with { Committed = w.Committed + quantity } : w)
                .ToList();

            _items[itemCode] = item with { ItemWarehouseInfoCollection = warehouses };
            return true;
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
