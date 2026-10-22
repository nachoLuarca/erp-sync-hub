using System.Globalization;
using SapB1.Mock.Admin;

namespace SapB1.Mock.BusinessPartners;

internal sealed class InMemoryBusinessPartnerStore : IBusinessPartnerStore, IResettable
{
    private readonly Lock _gate = new();
    private Dictionary<string, BusinessPartner> _partners = Seed();

    public IReadOnlyList<BusinessPartner> GetAll()
    {
        lock (_gate)
        {
            return [.. _partners.Values];
        }
    }

    public BusinessPartner? Find(string cardCode)
    {
        lock (_gate)
        {
            return _partners.GetValueOrDefault(cardCode);
        }
    }

    public BusinessPartner? Add(BusinessPartner partner)
    {
        lock (_gate)
        {
            var stored = string.IsNullOrWhiteSpace(partner.CardCode)
                ? partner with { CardCode = NextCardCode(partner.CardType) }
                : partner;

            return _partners.TryAdd(stored.CardCode, stored) ? stored : null;
        }
    }

    public bool Replace(BusinessPartner partner)
    {
        lock (_gate)
        {
            if (!_partners.ContainsKey(partner.CardCode))
            {
                return false;
            }

            _partners[partner.CardCode] = partner;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _partners = Seed();
        }
    }

    private static Dictionary<string, BusinessPartner> Seed() =>
        BusinessPartnerSeed.Create().ToDictionary(p => p.CardCode, StringComparer.Ordinal);

    /// <summary>Siguiente código de la serie del tipo: la letra de la serie más cinco dígitos.</summary>
    private string NextCardCode(string cardType)
    {
        var prefix = cardType switch
        {
            CardTypes.Supplier => 'P',
            CardTypes.Lead => 'L',
            _ => 'C',
        };

        var last = _partners.Keys
            .Where(code => code.Length == 6 && code[0] == prefix && code.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
            .Select(code => int.Parse(code.AsSpan(1), CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{(last + 1).ToString("D5", CultureInfo.InvariantCulture)}";
    }
}
