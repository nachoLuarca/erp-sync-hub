using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>
/// Guarda un enum como texto en <c>snake_case</c> (<c>InProgress</c> → <c>in_progress</c>), legible en la base
/// de datos y estable aunque cambie el orden de los miembros.
/// </summary>
internal sealed partial class SnakeCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(value => ToDatabase(value), text => FromDatabase(text))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ByText =
        Enum.GetValues<TEnum>().ToDictionary(value => ToDatabase(value), StringComparer.Ordinal);

    /// <summary>Valores que se aceptan en la base de datos, para armar las restricciones <c>CHECK</c>.</summary>
    public static IReadOnlyCollection<string> AllowedValues => ByText.Keys;

    public static string ToDatabase(TEnum value) =>
        WordBoundary().Replace(value.ToString(), "$1_$2").ToLowerInvariant();

    public static TEnum FromDatabase(string text) =>
        ByText.TryGetValue(text, out var value)
            ? value
            : throw new InvalidOperationException($"El valor '{text}' no es válido para {typeof(TEnum).Name}.");

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundary();
}
