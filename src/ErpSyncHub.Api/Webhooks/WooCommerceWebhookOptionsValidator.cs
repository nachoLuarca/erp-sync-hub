using ErpSyncHub.Application.WooCommerce;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Webhooks;

internal sealed class WooCommerceWebhookOptionsValidator : IValidateOptions<WooCommerceWebhookOptions>
{
    private const int MinimumBodyBytes = 1024;
    private const int MaximumBodyBytes = 10 * 1024 * 1024;

    public ValidateOptionsResult Validate(string? name, WooCommerceWebhookOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            errors.Add("Webhooks:WooCommerce:Secret es obligatorio (variable de entorno Webhooks__WooCommerce__Secret).");
        }
        else if (options.Secret.Length < WooCommerceWebhookOptions.MinimumSecretLength)
        {
            errors.Add(
                $"Webhooks:WooCommerce:Secret debe tener al menos {WooCommerceWebhookOptions.MinimumSecretLength} caracteres " +
                "(variable de entorno Webhooks__WooCommerce__Secret).");
        }

        if (options.MaxBodyBytes is < MinimumBodyBytes or > MaximumBodyBytes)
        {
            errors.Add($"Webhooks:WooCommerce:MaxBodyBytes debe estar entre {MinimumBodyBytes} y {MaximumBodyBytes} bytes.");
        }

        if (options.AcceptedStatuses.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Webhooks:WooCommerce:AcceptedStatuses no admite estados vacíos.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
