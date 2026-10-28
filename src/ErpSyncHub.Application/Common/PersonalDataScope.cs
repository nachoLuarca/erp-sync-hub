using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ErpSyncHub.Application.Common;

/// <summary>Qué clase de dato personal es un valor.</summary>
public enum PersonalDataKind
{
    Name,
    Address,
    Phone,
    Email,
    TaxId,
}

/// <summary>Un dato personal concreto del pedido en proceso.</summary>
public readonly record struct PersonalValue(PersonalDataKind Kind, string Value);

/// <summary>
/// Los datos personales del pedido que se está procesando, disponibles para ocultarlos en los mensajes que repiten los sistemas
/// externos. Los patrones de <see cref="PersonalDataRedactor"/> reconocen RUT y correos por su forma; un nombre o una dirección no
/// tienen forma, pero sí se conocen: son los del pedido. Aquí se buscan esos valores exactos, sin distinguir mayúsculas ni tildes ni
/// espacios repetidos (<c>"JOSÉ  Pérez"</c> encuentra <c>"jose perez"</c>), y los teléfonos por sus dígitos.
/// </summary>
/// <remarks>
/// El ámbito fluye con el flujo asíncrono de quien lo abre (<see cref="AsyncLocal{T}"/>): cada job tiene el suyo aunque varios
/// corran a la vez, y desaparece al cerrarlo. Es una defensa por coincidencia exacta: un dato escrito distinto por el sistema
/// externo (otro orden de las palabras, una abreviatura) no se reconoce.
/// </remarks>
public static partial class PersonalDataScope
{
    /// <summary>Largo mínimo de un valor para buscarlo: ocultar «Ana» o «12» en cualquier texto destruiría los mensajes sin proteger nada.</summary>
    public const int MinValueLength = 4;

    private const int MinPhoneDigits = 7;
    private const int MinPhoneSuffixDigits = 8;

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Abre un ámbito con los datos del pedido. Al cerrarlo (<c>Dispose</c>) se restaura el anterior.</summary>
    public static IDisposable Begin(IEnumerable<PersonalValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var previous = Current.Value;
        Current.Value = new Scope(values);
        return new Restore(previous);
    }

    /// <summary>Oculta los datos del ámbito actual en el texto. Sin ámbito, o sin coincidencias, lo devuelve igual.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || Current.Value is not { } scope)
        {
            return text;
        }

        return scope.Redact(text);
    }

    private static string Placeholder(PersonalDataKind kind) => kind switch
    {
        PersonalDataKind.Name => "[nombre]",
        PersonalDataKind.Address => "[dirección]",
        PersonalDataKind.Phone => "[teléfono]",
        PersonalDataKind.Email => PersonalDataRedactor.EmailPlaceholder,
        _ => PersonalDataRedactor.RutPlaceholder,
    };

    /// <summary>Texto normalizado (minúsculas, sin tildes, espacios colapsados) y, por cada carácter, el tramo del original del que viene.</summary>
    private sealed record Normalized(string Text, int[] Starts, int[] Ends);

    /// <summary>
    /// Letras con tilde o diéresis y su letra base. Se hace a mano y no con <c>string.Normalize</c>: el hub corre con
    /// <c>InvariantGlobalization</c> y en ese modo la descomposición Unicode no está disponible.
    /// </summary>
    private static readonly (string Accented, char Base)[] Folds =
    [
        ("àáâãäåāăą", 'a'),
        ("çćč", 'c'),
        ("ďđ", 'd'),
        ("èéêëēĕėęě", 'e'),
        ("ìíîïĩīĭįı", 'i'),
        ("ñńň", 'n'),
        ("òóôõöøōŏő", 'o'),
        ("ŕř", 'r'),
        ("śšş", 's'),
        ("ťţ", 't'),
        ("ùúûüũūŭůűų", 'u'),
        ("ýÿ", 'y'),
        ("źżž", 'z'),
    ];

    /// <summary>La letra en minúscula y sin tilde.</summary>
    private static char Fold(char letter)
    {
        var lower = char.ToLowerInvariant(letter);
        foreach (var (accented, baseLetter) in Folds)
        {
            if (accented.Contains(lower, StringComparison.Ordinal))
            {
                return baseLetter;
            }
        }

        return lower;
    }

    private static Normalized Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                if (builder.Length > 0 && builder[^1] == ' ')
                {
                    ends[^1] = index + 1;
                }
                else
                {
                    builder.Append(' ');
                    starts.Add(index);
                    ends.Add(index + 1);
                }

                continue;
            }

            // Una marca suelta (la tilde de un texto ya descompuesto) no cuenta: la letra que la lleva ya está.
            if (CharUnicodeInfo.GetUnicodeCategory(current) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(Fold(current));
            starts.Add(index);
            ends.Add(index + 1);
        }

        return new Normalized(builder.ToString(), [.. starts], [.. ends]);
    }

    private sealed class Scope
    {
        private readonly List<(PersonalDataKind Kind, string Text)> _textValues = [];
        private readonly List<string> _phoneDigits = [];

        public Scope(IEnumerable<PersonalValue> values)
        {
            foreach (var (kind, value) in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (kind == PersonalDataKind.Phone)
                {
                    var digits = DigitsOf(value);
                    if (digits.Length >= MinPhoneDigits && !_phoneDigits.Contains(digits))
                    {
                        _phoneDigits.Add(digits);
                    }

                    continue;
                }

                var normalized = Normalize(value).Text.Trim();
                if (normalized.Length >= MinValueLength && !_textValues.Contains((kind, normalized)))
                {
                    _textValues.Add((kind, normalized));
                }
            }

            // Los valores largos primero: «Av. Los Leones 123, Providencia» antes que «Los Leones 123».
            _textValues.Sort((left, right) => right.Text.Length.CompareTo(left.Text.Length));
        }

        public string Redact(string text)
        {
            var ranges = new List<(int Start, int End, PersonalDataKind Kind)>();

            if (_textValues.Count > 0)
            {
                var normalized = Normalize(text);
                foreach (var (kind, value) in _textValues)
                {
                    var from = 0;
                    while (from <= normalized.Text.Length - value.Length)
                    {
                        var found = normalized.Text.IndexOf(value, from, StringComparison.Ordinal);
                        if (found < 0)
                        {
                            break;
                        }

                        var last = found + value.Length - 1;
                        if (IsWordBoundary(normalized.Text, found - 1) && IsWordBoundary(normalized.Text, last + 1))
                        {
                            ranges.Add((normalized.Starts[found], normalized.Ends[last], kind));
                        }

                        from = found + 1;
                    }
                }
            }

            if (_phoneDigits.Count > 0)
            {
                foreach (Match candidate in PhoneLike().Matches(text))
                {
                    var digits = DigitsOf(candidate.Value);
                    if (_phoneDigits.Exists(phone => SamePhone(phone, digits)))
                    {
                        ranges.Add((candidate.Index, candidate.Index + candidate.Length, PersonalDataKind.Phone));
                    }
                }
            }

            return ranges.Count == 0 ? text : Apply(text, ranges);
        }

        /// <summary>Verdadero si no hay un carácter, o si el de esa posición no es letra ni dígito (el valor no está dentro de otra palabra).</summary>
        private static bool IsWordBoundary(string text, int index) =>
            index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);

        /// <summary>Mismo número aunque uno traiga el código de país (<c>+56 9 1234 5678</c> y <c>912345678</c>).</summary>
        private static bool SamePhone(string phone, string digits) =>
            phone == digits
            || (Math.Min(phone.Length, digits.Length) >= MinPhoneSuffixDigits && (phone.EndsWith(digits, StringComparison.Ordinal) || digits.EndsWith(phone, StringComparison.Ordinal)));

        private static string Apply(string text, List<(int Start, int End, PersonalDataKind Kind)> ranges)
        {
            ranges.Sort((left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));

            var builder = new StringBuilder(text.Length);
            var position = 0;
            foreach (var (start, end, kind) in ranges)
            {
                if (start < position)
                {
                    // Se solapa con un tramo ya oculto: si lo sobrepasa, se extiende el reemplazo hasta su final.
                    if (end > position)
                    {
                        position = end;
                    }

                    continue;
                }

                builder.Append(text, position, start - position).Append(Placeholder(kind));
                position = end;
            }

            return builder.Append(text, position, text.Length - position).ToString();
        }

        private static string DigitsOf(string value) => new([.. value.Where(char.IsAsciiDigit)]);
    }

    private sealed class Restore(Scope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }

    // Un teléfono escrito con espacios, guiones, paréntesis o puntos: +56 9 1234 5678, (2) 2345-6789.
    [GeneratedRegex(@"\+?\d[\d\s\-().]{5,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLike();
}
