using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;
using SapB1.Mock.OData;
using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.BusinessPartners;

internal static class BusinessPartnerEndpoints
{
    private const string EntitySet = "BusinessPartners";

    public static void MapBusinessPartners(this RouteGroupBuilder securedServiceLayer)
    {
        securedServiceLayer.MapGet(
            $"/{EntitySet}",
            (HttpContext http, IBusinessPartnerStore store, IOptions<MockOptions> options) =>
                ODataResults.Collection(
                    http,
                    EntitySet,
                    [.. store.GetAll().Select(SapJson.ToNode)],
                    options.Value.DefaultPageSize));

        securedServiceLayer.MapGet(
            $"/{EntitySet}('{{cardCode}}')",
            (HttpContext http, string cardCode, IBusinessPartnerStore store) =>
                store.Find(ODataKey.Unescape(cardCode)) is { } partner
                    ? ODataResults.Entity(http, EntitySet, SapJson.ToNode(partner))
                    : SapResults.NotFound());

        securedServiceLayer.MapPost($"/{EntitySet}", CreateAsync);
        securedServiceLayer.MapMethods($"/{EntitySet}('{{cardCode}}')", ["PATCH"], UpdateAsync);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        IBusinessPartnerStore store,
        CancellationToken cancellationToken)
    {
        var (body, failure) = await SapRequestBody.ReadObjectAsync<BusinessPartner>(http, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var (partner, conversionFailure) = SapRequestBody.ToEntity<BusinessPartner>(body!);
        if (conversionFailure is not null)
        {
            return conversionFailure;
        }

        partner = Normalize(partner!);
        if (BusinessPartnerValidator.Validate(partner) is { } validationError)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.Validation, validationError);
        }

        var created = store.Add(partner);
        return created is null
            ? SapResults.Error(
                StatusCodes.Status400BadRequest,
                SapErrorCodes.AlreadyExists,
                "This entry already exists in the following tables (ODBC -2035)")
            : ODataResults.Entity(http, EntitySet, SapJson.ToNode(created), StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext http,
        string cardCode,
        IBusinessPartnerStore store,
        CancellationToken cancellationToken)
    {
        cardCode = ODataKey.Unescape(cardCode);
        var existing = store.Find(cardCode);
        if (existing is null)
        {
            return SapResults.NotFound();
        }

        var (body, failure) = await SapRequestBody.ReadObjectAsync<BusinessPartner>(http, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (body!.TryGetPropertyValue(nameof(BusinessPartner.CardCode), out var newCode)
            && (newCode is not JsonValue value || !value.TryGetValue(out string? code) || code != cardCode))
        {
            return SapResults.Error(
                StatusCodes.Status400BadRequest,
                SapErrorCodes.Validation,
                "Field 'CardCode' cannot be modified");
        }

        // PATCH reemplaza solo las propiedades enviadas: se mezcla sobre el estado actual y se revalida completo.
        var merged = SapJson.ToNode(existing);
        foreach (var property in body)
        {
            merged[property.Key] = property.Value?.DeepClone();
        }

        var (updated, conversionFailure) = SapRequestBody.ToEntity<BusinessPartner>(merged);
        if (conversionFailure is not null)
        {
            return conversionFailure;
        }

        updated = Normalize(updated!);
        if (BusinessPartnerValidator.Validate(updated) is { } validationError)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.Validation, validationError);
        }

        store.Replace(updated);
        return Results.NoContent();
    }

    /// <summary>Un JSON con <c>null</c> explícito deja la colección nula; se normaliza a vacía.</summary>
    private static BusinessPartner Normalize(BusinessPartner partner) =>
        partner.BPAddresses is null ? partner with { BPAddresses = [] } : partner;
}
