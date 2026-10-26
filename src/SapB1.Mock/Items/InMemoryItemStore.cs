using SapB1.Mock.Admin;

namespace SapB1.Mock.Items;

internal sealed class InMemoryItemStore : IItemStore, IResettable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private Dictionary<string, Item> _items;

    public InMemoryItemStore(TimeProvider time)
    {
        _time = time;
        _items = Seed();
    }

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

            _items[itemCode] = Stamp(item with { ItemWarehouseInfoCollection = warehouses });
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

    /// <summary>Fija la fecha y la hora de modificación en la hora local del reloj, como hace SAP en cada cambio del artículo.</summary>
    private Item Stamp(Item item)
    {
        var now = _time.GetLocalNow();
        return item with { UpdateDate = DateOnly.FromDateTime(now.DateTime), UpdateTime = TimeOnly.FromDateTime(now.DateTime) };
    }

    private Dictionary<string, Item> Seed() =>
        ItemSeed.Create(_time.GetLocalNow()).ToDictionary(item => item.ItemCode, StringComparer.Ordinal);
}
