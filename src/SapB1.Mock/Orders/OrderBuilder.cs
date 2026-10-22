using SapB1.Mock.BusinessPartners;
using SapB1.Mock.Items;
using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Orders;

/// <summary>
/// Valida una orden de venta recibida y le calcula los datos que asigna SAP: nombre del cliente,
/// descripciones, precios por defecto, totales y fechas.
/// </summary>
internal sealed class OrderBuilder(IBusinessPartnerStore partners, IItemStore items, TimeProvider time)
{
    public const int MaxNumAtCardLength = 100;
    public const int MaxCommentsLength = 254;

    /// <summary>Devuelve la orden completa o el mensaje del primer error de validación.</summary>
    public (Order? Order, string? Error) Build(Order input)
    {
        if (string.IsNullOrWhiteSpace(input.CardCode))
        {
            return (null, "Field 'CardCode' is required");
        }

        var partner = partners.Find(input.CardCode);
        if (partner is null)
        {
            return (null, $"Business partner '{input.CardCode}' does not exist");
        }

        if (partner.CardType == CardTypes.Supplier)
        {
            return (null, $"Business partner '{partner.CardCode}' is a vendor and cannot be used in a sales document");
        }

        if (partner.Frozen == YesNo.Yes || partner.Valid == YesNo.No)
        {
            return (null, $"Business partner '{partner.CardCode}' is inactive");
        }

        if (input.NumAtCard is { Length: > MaxNumAtCardLength })
        {
            return (null, $"Field 'NumAtCard' exceeds {MaxNumAtCardLength} characters");
        }

        if (input.Comments is { Length: > MaxCommentsLength })
        {
            return (null, $"Field 'Comments' exceeds {MaxCommentsLength} characters");
        }

        var (lines, lineError) = BuildLines(input.DocumentLines);
        if (lineError is not null)
        {
            return (null, lineError);
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var docDate = input.DocDate ?? today;

        var order = new Order
        {
            DocDate = docDate,
            DocDueDate = input.DocDueDate ?? docDate,
            CardCode = partner.CardCode,
            CardName = partner.CardName,
            NumAtCard = input.NumAtCard,
            Comments = input.Comments,
            DocumentLines = lines!,
            DocTotal = lines!.Sum(line => line.LineTotal),
        };

        return (order, null);
    }

    private (List<DocumentLine>? Lines, string? Error) BuildLines(IReadOnlyList<DocumentLine>? input)
    {
        if (input is null || input.Count == 0)
        {
            return (null, "Field 'DocumentLines' requires at least one line");
        }

        var lines = new List<DocumentLine>(input.Count);

        for (var index = 0; index < input.Count; index++)
        {
            var line = input[index];
            var position = $"line {index}";

            var item = line is null || string.IsNullOrWhiteSpace(line.ItemCode) ? null : items.Find(line.ItemCode);
            if (line is null || item is null)
            {
                return (null, $"Item '{line?.ItemCode}' does not exist ({position})");
            }

            if (line.Quantity is null or <= 0)
            {
                return (null, $"Quantity must be greater than zero ({position})");
            }

            if (line.UnitPrice is < 0)
            {
                return (null, $"UnitPrice cannot be negative ({position})");
            }

            var warehouse = line.WarehouseCode ?? ItemSeed.DefaultWarehouse;
            if (item.ItemWarehouseInfoCollection.All(w => w.WarehouseCode != warehouse))
            {
                return (null, $"Warehouse '{warehouse}' is not valid for item '{item.ItemCode}' ({position})");
            }

            var unitPrice = line.UnitPrice ?? BasePrice(item);
            lines.Add(new DocumentLine
            {
                LineNum = index,
                ItemCode = item.ItemCode,
                ItemDescription = item.ItemName,
                Quantity = line.Quantity,
                UnitPrice = unitPrice,
                LineTotal = line.Quantity.Value * unitPrice,
                WarehouseCode = warehouse,
            });
        }

        return (lines, null);
    }

    private static decimal BasePrice(Item item) =>
        item.ItemPrices.FirstOrDefault(price => price.PriceList == ItemSeed.BasePriceList)?.Price ?? 0;
}
