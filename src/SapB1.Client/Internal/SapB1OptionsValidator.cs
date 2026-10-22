using Microsoft.Extensions.Options;

namespace SapB1.Client.Internal;

internal sealed class SapB1OptionsValidator : IValidateOptions<SapB1Options>
{
    public ValidateOptionsResult Validate(string? name, SapB1Options options)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https"))
        {
            errors.Add($"{nameof(SapB1Options.BaseUrl)} must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(options.CompanyDb))
        {
            errors.Add($"{nameof(SapB1Options.CompanyDb)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.UserName))
        {
            errors.Add($"{nameof(SapB1Options.UserName)} is required.");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            errors.Add($"{nameof(SapB1Options.Password)} is required.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(SapB1Options.RequestTimeout)} must be greater than zero.");
        }

        if (options.PageSize < 1)
        {
            errors.Add($"{nameof(SapB1Options.PageSize)} must be at least 1.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
