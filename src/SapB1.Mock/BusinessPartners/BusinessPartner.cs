using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.BusinessPartners;

/// <summary>Socio de negocios (subconjunto del objeto <c>BusinessPartners</c> del Service Layer).</summary>
internal sealed record BusinessPartner
{
    public string CardCode { get; init; } = string.Empty;

    public string CardName { get; init; } = string.Empty;

    /// <summary><c>cCustomer</c>, <c>cSupplier</c> o <c>cLid</c> (lead).</summary>
    public string CardType { get; init; } = CardTypes.Customer;

    /// <summary>Identificador tributario (RUT en Chile).</summary>
    public string? FederalTaxID { get; init; }

    public string? EmailAddress { get; init; }

    public string? Phone1 { get; init; }

    public string Valid { get; init; } = YesNo.Yes;

    public string Frozen { get; init; } = YesNo.No;

    public IReadOnlyList<BPAddress> BPAddresses { get; init; } = [];
}

/// <summary>Dirección de facturación (<c>bo_BillTo</c>) o de despacho (<c>bo_ShipTo</c>).</summary>
internal sealed record BPAddress
{
    public string AddressName { get; init; } = string.Empty;

    public string AddressType { get; init; } = AddressTypes.ShipTo;

    public string? Street { get; init; }

    public string? City { get; init; }

    public string? Country { get; init; }

    public string? ZipCode { get; init; }
}

internal static class CardTypes
{
    public const string Customer = "cCustomer";
    public const string Supplier = "cSupplier";
    public const string Lead = "cLid";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Customer,
        Supplier,
        Lead,
    };
}

internal static class AddressTypes
{
    public const string ShipTo = "bo_ShipTo";
    public const string BillTo = "bo_BillTo";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ShipTo,
        BillTo,
    };
}
