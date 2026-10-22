namespace SapB1.Client.Models;

/// <summary>
/// Socio de negocios (objeto <c>BusinessPartners</c>). Si la consulta usó <c>Select</c>,
/// las propiedades no pedidas quedan con su valor por defecto.
/// </summary>
public sealed record BusinessPartner
{
    public string CardCode { get; init; } = string.Empty;

    public string? CardName { get; init; }

    /// <summary>Ver <see cref="CardTypes"/>.</summary>
    public string? CardType { get; init; }

    /// <summary>Identificador tributario (RUT en Chile).</summary>
    public string? FederalTaxID { get; init; }

    public string? EmailAddress { get; init; }

    public string? Phone1 { get; init; }

    /// <summary>Habilitado: <see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Valid { get; init; }

    /// <summary>Congelado: <see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Frozen { get; init; }

    public IReadOnlyList<BpAddress> BPAddresses { get; init; } = [];
}

/// <summary>Dirección de un socio de negocios.</summary>
public sealed record BpAddress
{
    public string AddressName { get; init; } = string.Empty;

    /// <summary>Ver <see cref="AddressTypes"/>.</summary>
    public string? AddressType { get; init; }

    public string? Street { get; init; }

    public string? City { get; init; }

    /// <summary>Código de país, por ejemplo <c>CL</c>.</summary>
    public string? Country { get; init; }

    public string? ZipCode { get; init; }
}

/// <summary>Valores del enumerado <c>BoCardTypes</c>.</summary>
public static class CardTypes
{
    public const string Customer = "cCustomer";
    public const string Supplier = "cSupplier";
    public const string Lead = "cLid";
}

/// <summary>Valores del enumerado <c>BoAddressType</c>.</summary>
public static class AddressTypes
{
    public const string ShipTo = "bo_ShipTo";
    public const string BillTo = "bo_BillTo";
}
