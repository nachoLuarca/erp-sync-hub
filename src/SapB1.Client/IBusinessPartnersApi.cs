using SapB1.Client.Models;

namespace SapB1.Client;

/// <summary>Socios de negocios (<c>BusinessPartners</c>) del Service Layer.</summary>
public interface IBusinessPartnersApi
{
    /// <summary>Obtiene un socio por su código, o <see langword="null"/> si no existe.</summary>
    Task<BusinessPartner?> GetAsync(string cardCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista socios. Para buscar por RUT o correo, use <see cref="SapQuery.Filter"/> con <see cref="SapFilter"/>.
    /// </summary>
    IAsyncEnumerable<BusinessPartner> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Crea un socio y devuelve el registro creado, con el <c>CardCode</c> asignado.</summary>
    /// <exception cref="SapB1ApiException">El código ya existe (<see cref="SapB1ApiException.IsAlreadyExists"/>) o los datos no son válidos.</exception>
    Task<BusinessPartner> CreateAsync(CreateBusinessPartnerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Modifica solo las propiedades con valor en <paramref name="request"/>.</summary>
    /// <exception cref="SapB1ApiException">El socio no existe (<see cref="SapB1ApiException.IsNotFound"/>) o los datos no son válidos.</exception>
    Task UpdateAsync(string cardCode, UpdateBusinessPartnerRequest request, CancellationToken cancellationToken = default);
}
