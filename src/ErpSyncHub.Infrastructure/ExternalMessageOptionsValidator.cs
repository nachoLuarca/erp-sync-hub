using ErpSyncHub.Application.Common;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure;

internal sealed class ExternalMessageOptionsValidator : IValidateOptions<ExternalMessageOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalMessageOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
