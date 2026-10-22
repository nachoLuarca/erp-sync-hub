namespace SapB1.Mock.BusinessPartners;

/// <summary>Reglas de negocio mínimas para crear o modificar un socio de negocios.</summary>
internal static class BusinessPartnerValidator
{
    /// <summary>Devuelve el mensaje de error, o <see langword="null"/> si el socio es válido.</summary>
    public static string? Validate(BusinessPartner partner)
    {
        if (string.IsNullOrWhiteSpace(partner.CardName))
        {
            return "Field 'CardName' is required";
        }

        if (partner.CardType is null || !CardTypes.All.Contains(partner.CardType))
        {
            return $"Value '{partner.CardType}' is invalid for field 'CardType'";
        }

        if (partner.Valid is not (YesNo.Yes or YesNo.No))
        {
            return $"Value '{partner.Valid}' is invalid for field 'Valid'";
        }

        if (partner.Frozen is not (YesNo.Yes or YesNo.No))
        {
            return $"Value '{partner.Frozen}' is invalid for field 'Frozen'";
        }

        foreach (var address in partner.BPAddresses ?? [])
        {
            if (address is null || string.IsNullOrWhiteSpace(address.AddressName))
            {
                return "Field 'AddressName' is required";
            }

            if (address.AddressType is null || !AddressTypes.All.Contains(address.AddressType))
            {
                return $"Value '{address.AddressType}' is invalid for field 'AddressType'";
            }
        }

        return null;
    }
}
