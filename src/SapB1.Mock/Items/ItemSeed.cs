using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Items;

/// <summary>
/// Datos semilla del maestro de artículos: ferretería con precios en pesos chilenos.
/// Son 24 artículos para que la paginación por defecto (20) produzca más de una página.
/// </summary>
internal static class ItemSeed
{
    public const string Currency = "CLP";
    public const string DefaultWarehouse = "01";
    public const int BasePriceList = 1;

    private static readonly (string Code, string Name, decimal Price, decimal Stock)[] Catalog =
    [
        ("A00001", "Taladro percutor 650 W", 39990, 45),
        ("A00002", "Atornillador inalámbrico 12 V", 29990, 60),
        ("A00003", "Sierra circular 1200 W", 54990, 18),
        ("A00004", "Esmeril angular 4 1/2\"", 24990, 32),
        ("A00005", "Lijadora orbital 240 W", 27990, 25),
        ("A00006", "Juego de llaves combinadas 12 piezas", 17990, 80),
        ("A00007", "Martillo carpintero 16 oz", 6990, 120),
        ("A00008", "Set de destornilladores 6 piezas", 4990, 200),
        ("A00009", "Alicate universal 8\"", 5490, 150),
        ("A00010", "Huincha de medir 5 m", 2990, 300),
        ("A00011", "Nivel de burbuja 60 cm", 7990, 70),
        ("A00012", "Caja de herramientas 19\"", 12990, 40),
        ("A00013", "Pintura látex blanco 1 galón", 15990, 90),
        ("A00014", "Rodillo para pintura 9\"", 3490, 140),
        ("A00015", "Brocha 3\"", 1990, 220),
        ("A00016", "Cinta de enmascarar 48 mm", 1290, 400),
        ("A00017", "Guantes de seguridad", 2490, 250),
        ("A00018", "Lentes de seguridad", 1990, 180),
        ("A00019", "Casco de seguridad", 4990, 75),
        ("A00020", "Extensión eléctrica 5 m", 6990, 110),
        ("A00021", "Ampolleta LED 9 W", 1490, 500),
        ("A00022", "Cinta aisladora negra", 990, 600),
        ("A00023", "Candado 40 mm", 3990, 130),
        ("A00024", "Adhesivo de contacto 250 ml", 3290, 0),
    ];

    public static IReadOnlyList<Item> Create() =>
        [.. Catalog.Select(entry => new Item(
            entry.Code,
            entry.Name,
            Valid: YesNo.Yes,
            Frozen: YesNo.No,
            ItemPrices: [new ItemPrice(BasePriceList, entry.Price, Currency)],
            ItemWarehouseInfoCollection: [new ItemWarehouseInfo(DefaultWarehouse, entry.Stock, 0, 0)]))];
}
