using ErpSyncHub.Domain.Customers;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Sap;

/// <summary>
/// Cómo el hub conversa con SAP más allá de la conexión (que configura <c>SapB1.Client</c>). Comparte la sección
/// <c>SAP</c> de la configuración.
/// </summary>
public sealed class SapGatewayOptions
{
    /// <summary>
    /// Formato del RUT en <c>FederalTaxID</c> (<c>LicTradNum</c>): el que el hub <b>envía</b> al crear un cliente y el que
    /// <b>busca</b> al localizarlo. Por defecto, con puntos (<c>12.345.678-5</c>). Debe coincidir con cómo se guardan los RUT en SAP:
    /// los clientes guardados en el otro formato no se encontrarían.
    /// </summary>
    public TaxIdFormat TaxIdFormat { get; set; } = TaxIdFormat.WithDots;
}

internal sealed class SapGatewayOptionsValidator : IValidateOptions<SapGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, SapGatewayOptions options) =>
        Enum.IsDefined(options.TaxIdFormat)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("SAP:TaxIdFormat debe ser WithDots o WithoutDots (variable de entorno SAP__TaxIdFormat).");
}
