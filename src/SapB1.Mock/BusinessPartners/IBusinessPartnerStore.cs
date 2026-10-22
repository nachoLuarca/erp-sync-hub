namespace SapB1.Mock.BusinessPartners;

/// <summary>Persistencia de los socios de negocios del mock.</summary>
internal interface IBusinessPartnerStore
{
    IReadOnlyList<BusinessPartner> GetAll();

    BusinessPartner? Find(string cardCode);

    /// <summary>
    /// Agrega el socio. Si <c>CardCode</c> viene vacío se genera el siguiente de la serie
    /// (C, P o L según el tipo). Devuelve <see langword="null"/> si el código ya existe.
    /// </summary>
    BusinessPartner? Add(BusinessPartner partner);

    /// <summary>Reemplaza un socio existente. Devuelve <see langword="false"/> si no existe.</summary>
    bool Replace(BusinessPartner partner);
}
