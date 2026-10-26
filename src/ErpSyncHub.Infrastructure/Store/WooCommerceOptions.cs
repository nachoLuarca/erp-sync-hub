using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Store;

/// <summary>
/// Cómo el hub llama a la API REST de WooCommerce (sección <c>Store:WooCommerce</c>, variables
/// <c>Store__WooCommerce__*</c>). Si no se define ninguna, la tienda se considera no configurada y la sincronización de stock
/// y precios queda desactivada.
/// </summary>
public sealed class WooCommerceOptions
{
    public const string SectionName = "Store:WooCommerce";

    /// <summary>Máximo de elementos por lote que acepta WooCommerce por defecto (<c>woocommerce_rest_batch_items_limit</c>).</summary>
    public const int MaxBatchSize = 100;

    /// <summary>Dirección de la tienda, por ejemplo <c>https://tienda.example.com</c> (sin <c>/wp-json</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Clave de consumidor de la API REST: <c>ck_</c> y 40 caracteres hexadecimales.</summary>
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>Secreto de consumidor de la API REST: <c>cs_</c> y 40 caracteres hexadecimales.</summary>
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>Productos por lote al actualizar. No puede superar el límite de la tienda.</summary>
    public int BatchSize { get; set; } = MaxBatchSize;

    /// <summary>Tiempo máximo de cada petición a la tienda.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Verdadero si se definió algo: entonces las opciones obligatorias se validan al iniciar.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) || !string.IsNullOrWhiteSpace(ConsumerKey) || !string.IsNullOrWhiteSpace(ConsumerSecret);

    /// <summary>La dirección base como URI absoluta terminada en barra, o <see langword="null"/> si no es válida.</summary>
    public Uri? BaseUri =>
        Uri.TryCreate(BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
}

internal sealed partial class WooCommerceOptionsValidator : IValidateOptions<WooCommerceOptions>
{
    public ValidateOptionsResult Validate(string? name, WooCommerceOptions options)
    {
        if (!options.IsConfigured)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (options.BaseUri is null)
        {
            errors.Add("Store:WooCommerce:BaseUrl debe ser una URL http o https absoluta (variable de entorno Store__WooCommerce__BaseUrl).");
        }

        if (!ConsumerKeyPattern().IsMatch(options.ConsumerKey))
        {
            errors.Add("Store:WooCommerce:ConsumerKey debe tener el formato ck_ seguido de 40 caracteres hexadecimales (variable de entorno Store__WooCommerce__ConsumerKey).");
        }

        if (!ConsumerSecretPattern().IsMatch(options.ConsumerSecret))
        {
            errors.Add("Store:WooCommerce:ConsumerSecret debe tener el formato cs_ seguido de 40 caracteres hexadecimales (variable de entorno Store__WooCommerce__ConsumerSecret).");
        }

        if (options.BatchSize is < 1 or > WooCommerceOptions.MaxBatchSize)
        {
            errors.Add($"Store:WooCommerce:BatchSize debe estar entre 1 y {WooCommerceOptions.MaxBatchSize}, el límite de WooCommerce por lote (variable de entorno Store__WooCommerce__BatchSize).");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            errors.Add("Store:WooCommerce:RequestTimeout debe ser mayor que cero (variable de entorno Store__WooCommerce__RequestTimeout).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    [GeneratedRegex("^ck_[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConsumerKeyPattern();

    [GeneratedRegex("^cs_[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConsumerSecretPattern();
}
