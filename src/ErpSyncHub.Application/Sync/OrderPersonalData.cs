using System.Globalization;
using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Customers;

namespace ErpSyncHub.Application.Sync;

/// <summary>Los datos personales de un pedido, para ocultarlos si un sistema externo los repite en un mensaje (ver <see cref="PersonalDataScope"/>).</summary>
internal static class OrderPersonalData
{
    /// <param name="order">El pedido en proceso.</param>
    /// <param name="taxIdMetaKey">Metadato del pedido donde la tienda guarda el RUT; vacío si no se usa.</param>
    public static IEnumerable<PersonalValue> Of(WooCommerceOrder order, string taxIdMetaKey)
    {
        foreach (var address in new[] { order.Billing, order.Shipping })
        {
            yield return new PersonalValue(PersonalDataKind.Name, address.FullName);
            yield return new PersonalValue(PersonalDataKind.Name, address.Company);
            yield return new PersonalValue(PersonalDataKind.Address, address.Address1);
            yield return new PersonalValue(PersonalDataKind.Address, address.Address2);
            yield return new PersonalValue(PersonalDataKind.Email, address.Email);
            yield return new PersonalValue(PersonalDataKind.Phone, address.Phone);
        }

        var rawTaxId = taxIdMetaKey.Length > 0 ? order.GetMeta(taxIdMetaKey) : null;
        if (rawTaxId is null)
        {
            yield break;
        }

        yield return new PersonalValue(PersonalDataKind.TaxId, rawTaxId);

        // El mismo RUT escrito de las otras maneras en que SAP o la tienda pueden repetirlo.
        if (Rut.TryParse(rawTaxId, out var rut))
        {
            yield return new PersonalValue(PersonalDataKind.TaxId, rut.Normalized);
            yield return new PersonalValue(PersonalDataKind.TaxId, rut.Formatted);
            yield return new PersonalValue(PersonalDataKind.TaxId, string.Create(CultureInfo.InvariantCulture, $"{rut.Number}{rut.CheckDigit}"));
        }
    }
}
