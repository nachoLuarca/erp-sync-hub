namespace SapB1.Mock.BusinessPartners;

/// <summary>Datos semilla: clientes y un proveedor ficticios, con RUT para probar la búsqueda por identificador tributario.</summary>
internal static class BusinessPartnerSeed
{
    public static IReadOnlyList<BusinessPartner> Create() =>
    [
        Customer("C00001", "Constructora Los Andes SpA", "76.123.456-7", "compras@losandes.example.com", "+56 2 2345 6789", "Av. Providencia 1234", "Providencia"),
        Customer("C00002", "Ferretería El Maestro Ltda.", "77.234.567-8", "contacto@elmaestro.example.com", "+56 32 255 0101", "Calle Valparaíso 567", "Viña del Mar"),
        Customer("C00003", "María Fernández Soto", "12.345.678-5", "maria.fernandez@example.com", "+56 9 8765 4321", "Pasaje Los Aromos 45", "Ñuñoa"),
        Customer("C00004", "Inmobiliaria Pacífico S.A.", "96.345.678-K", "adquisiciones@pacifico.example.com", "+56 41 222 3344", "Av. O'Higgins 890", "Concepción"),
        Customer("C00005", "Taller Mecánico Rojas", "15.678.901-2", "taller.rojas@example.com", "+56 9 1234 5678", "Camino a Melipilla 7800", "Maipú"),
        Customer("C00006", "Comercial Austral Ltda.", "78.456.789-0", "ventas@austral.example.com", "+56 61 224 5566", "Av. España 321", "Punta Arenas"),
        new BusinessPartner
        {
            CardCode = "P00001",
            CardName = "Importadora Herramientas del Sur SpA",
            CardType = CardTypes.Supplier,
            FederalTaxID = "76.987.654-3",
            EmailAddress = "pedidos@herramientasdelsur.example.com",
            Phone1 = "+56 2 2987 6543",
        },
    ];

    private static BusinessPartner Customer(
        string code,
        string name,
        string taxId,
        string email,
        string phone,
        string street,
        string city) =>
        new()
        {
            CardCode = code,
            CardName = name,
            FederalTaxID = taxId,
            EmailAddress = email,
            Phone1 = phone,
            BPAddresses =
            [
                new BPAddress { AddressName = "Facturación", AddressType = AddressTypes.BillTo, Street = street, City = city, Country = "CL" },
                new BPAddress { AddressName = "Despacho", AddressType = AddressTypes.ShipTo, Street = street, City = city, Country = "CL" },
            ],
        };
}
