namespace SapB1.Client.Models;

/// <summary>Datos para crear un socio de negocios.</summary>
public sealed record CreateBusinessPartnerRequest
{
    /// <summary>Código del socio. Si se omite, SAP asigna el siguiente de la serie de numeración.</summary>
    public string? CardCode { get; init; }

    public required string CardName { get; init; }

    /// <summary>Ver <see cref="CardTypes"/>. Por defecto, cliente.</summary>
    public string CardType { get; init; } = CardTypes.Customer;

    public string? FederalTaxID { get; init; }

    public string? EmailAddress { get; init; }

    public string? Phone1 { get; init; }

    public IReadOnlyList<BpAddress>? BPAddresses { get; init; }
}

/// <summary>
/// Cambios sobre un socio de negocios. Solo se envían las propiedades con valor: una propiedad en
/// <see langword="null"/> queda como está, por lo que este tipo no permite borrar un campo.
/// </summary>
public sealed record UpdateBusinessPartnerRequest
{
    public string? CardName { get; init; }

    public string? FederalTaxID { get; init; }

    public string? EmailAddress { get; init; }

    public string? Phone1 { get; init; }

    /// <summary><see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Valid { get; init; }

    /// <summary><see cref="YesNo.Yes"/> o <see cref="YesNo.No"/>.</summary>
    public string? Frozen { get; init; }

    /// <summary>Reemplaza la lista completa de direcciones.</summary>
    public IReadOnlyList<BpAddress>? BPAddresses { get; init; }
}
