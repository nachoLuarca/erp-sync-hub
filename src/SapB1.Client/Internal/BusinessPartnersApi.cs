using SapB1.Client.Models;

namespace SapB1.Client.Internal;

internal sealed class BusinessPartnersApi(SapB1Transport transport) : IBusinessPartnersApi
{
    private const string EntitySet = "BusinessPartners";

    public Task<BusinessPartner?> GetAsync(string cardCode, CancellationToken cancellationToken = default) =>
        transport.GetAsync<BusinessPartner>(SapKey.Text(EntitySet, cardCode), cancellationToken);

    public IAsyncEnumerable<BusinessPartner> QueryAsync(SapQuery? query = null, CancellationToken cancellationToken = default) =>
        transport.QueryAsync<BusinessPartner>(EntitySet, query, cancellationToken);

    public Task<BusinessPartner> CreateAsync(CreateBusinessPartnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return transport.PostAsync<CreateBusinessPartnerRequest, BusinessPartner>(EntitySet, request, cancellationToken);
    }

    public Task UpdateAsync(string cardCode, UpdateBusinessPartnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return transport.PatchAsync(SapKey.Text(EntitySet, cardCode), request, cancellationToken);
    }
}
