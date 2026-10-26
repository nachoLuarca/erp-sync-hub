using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

/// <summary>
/// Claves de acceso al panel (<c>/admin/*</c>), sección <c>Admin</c> (variable <c>Admin__ApiKey</c>; en Compose,
/// <c>ERPSYNC_ADMIN_API_KEY</c>). Admite varias claves separadas por coma para poder rotarlas sin cortar el acceso.
/// </summary>
public sealed class AdminApiKeyOptions
{
    public const string SectionName = "Admin";

    /// <summary>Largo mínimo de cada clave: 32 caracteres, unos 190 bits con una clave aleatoria en hexadecimal o base64.</summary>
    public const int MinKeyLength = 32;

    /// <summary>Largo máximo de cada clave; acota el trabajo de comparar una cabecera hostil.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>
    /// Una o más claves separadas por coma. Sin ninguna, el panel queda deshabilitado (responde 503) y el resto del hub funciona
    /// con normalidad.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Las claves configuradas, sin espacios en los extremos ni elementos vacíos (una coma de más no cuenta).</summary>
    public IReadOnlyList<string> Keys =>
        [.. (ApiKey ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

internal sealed class AdminApiKeyOptionsValidator : IValidateOptions<AdminApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminApiKeyOptions options)
    {
        var errors = new List<string>();
        var keys = options.Keys;

        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            var position = keys.Count > 1 ? $" (la clave n.º {index + 1})" : string.Empty;

            if (key.Length < AdminApiKeyOptions.MinKeyLength)
            {
                errors.Add(
                    $"Admin:ApiKey{position} debe tener al menos {AdminApiKeyOptions.MinKeyLength} caracteres (variable de entorno Admin__ApiKey, " +
                    "en Compose ERPSYNC_ADMIN_API_KEY). Genera una aleatoria como se explica en docs/variables-de-entorno.md.");
            }
            else if (key.Length > AdminApiKeyOptions.MaxKeyLength)
            {
                errors.Add($"Admin:ApiKey{position} admite hasta {AdminApiKeyOptions.MaxKeyLength} caracteres (variable de entorno Admin__ApiKey).");
            }
            else if (key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            {
                errors.Add($"Admin:ApiKey{position} no puede contener espacios ni caracteres de control (variable de entorno Admin__ApiKey).");
            }
        }

        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
        {
            errors.Add("Admin:ApiKey tiene la misma clave repetida (variable de entorno Admin__ApiKey).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
