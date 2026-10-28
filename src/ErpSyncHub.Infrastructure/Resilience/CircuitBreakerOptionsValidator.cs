using ErpSyncHub.Application.Resilience;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Resilience;

internal sealed class CircuitBreakerOptionsValidator : IValidateOptions<CircuitBreakerOptions>
{
    public ValidateOptionsResult Validate(string? name, CircuitBreakerOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
