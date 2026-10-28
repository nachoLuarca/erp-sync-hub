using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

/// <summary>
/// Claves de acceso al panel (<c>/admin/*</c>), sección <c>Admin</c>. Hay dos niveles: las claves de lectura
/// (<c>Admin__ReadApiKeys</c>; en Compose, <c>ERPSYNC_ADMIN_READ_API_KEYS</c>) consultan jobs, detalle y métricas, y las de operación
/// (<c>Admin__OperateApiKeys</c>; <c>ERPSYNC_ADMIN_OPERATE_API_KEYS</c>) además ejecutan acciones: reintentar, sincronizar, cerrar
/// el circuito y eliminar mapeos. Cada variable admite varias claves separadas por coma para rotarlas sin cortar el acceso.
/// </summary>
public sealed class AdminApiKeyOptions
{
    public const string SectionName = "Admin";

    /// <summary>Largo mínimo de cada clave: 32 caracteres, unos 190 bits con una clave aleatoria en hexadecimal o base64.</summary>
    public const int MinKeyLength = 32;

    /// <summary>Largo máximo de cada clave; acota el trabajo de comparar una cabecera hostil.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>Claves de solo lectura, separadas por coma.</summary>
    public string ReadApiKeys { get; set; } = string.Empty;

    /// <summary>
    /// Claves de operación (también leen), separadas por coma. Sin ninguna clave en ninguna de las dos listas, el panel queda
    /// deshabilitado (responde 503) y el resto del hub funciona con normalidad.
    /// </summary>
    public string OperateApiKeys { get; set; } = string.Empty;

    /// <summary>Las claves de lectura, sin espacios en los extremos ni elementos vacíos (una coma de más no cuenta).</summary>
    public IReadOnlyList<string> ReadKeys => Split(ReadApiKeys);

    /// <summary>Las claves de operación, con el mismo tratamiento.</summary>
    public IReadOnlyList<string> OperateKeys => Split(OperateApiKeys);

    private static string[] Split(string? value) =>
        [.. (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

internal sealed class AdminApiKeyOptionsValidator : IValidateOptions<AdminApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminApiKeyOptions options)
    {
        var errors = new List<string>();
        ValidateList(errors, options.ReadKeys, "Admin:ReadApiKeys", "Admin__ReadApiKeys", "ERPSYNC_ADMIN_READ_API_KEYS");
        ValidateList(errors, options.OperateKeys, "Admin:OperateApiKeys", "Admin__OperateApiKeys", "ERPSYNC_ADMIN_OPERATE_API_KEYS");

        // Una clave en las dos listas dejaría ambiguo qué puede hacer.
        if (options.ReadKeys.Intersect(options.OperateKeys, StringComparer.Ordinal).Any())
        {
            errors.Add(
                "Una misma clave está en Admin:ReadApiKeys y en Admin:OperateApiKeys (variables de entorno Admin__ReadApiKeys y " +
                "Admin__OperateApiKeys): cada clave tiene un solo nivel de acceso.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateList(List<string> errors, IReadOnlyList<string> keys, string setting, string variable, string composeVariable)
    {
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            var position = keys.Count > 1 ? $" (la clave n.º {index + 1})" : string.Empty;

            if (key.Length < AdminApiKeyOptions.MinKeyLength)
            {
                errors.Add(
                    $"{setting}{position} debe tener al menos {AdminApiKeyOptions.MinKeyLength} caracteres (variable de entorno {variable}, " +
                    $"en Compose {composeVariable}). Genera una aleatoria como se explica en docs/variables-de-entorno.md.");
            }
            else if (key.Length > AdminApiKeyOptions.MaxKeyLength)
            {
                errors.Add($"{setting}{position} admite hasta {AdminApiKeyOptions.MaxKeyLength} caracteres (variable de entorno {variable}).");
            }
            else if (key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            {
                errors.Add($"{setting}{position} no puede contener espacios ni caracteres de control (variable de entorno {variable}).");
            }
        }

        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
        {
            errors.Add($"{setting} tiene la misma clave repetida (variable de entorno {variable}).");
        }
    }
}
