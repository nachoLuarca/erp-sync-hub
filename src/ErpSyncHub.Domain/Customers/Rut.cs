using System.Globalization;

namespace ErpSyncHub.Domain.Customers;

/// <summary>
/// Rol Único Tributario chileno, validado con su dígito verificador (módulo 11).
/// </summary>
public readonly record struct Rut
{
    private const int MaxNumber = 99_999_999;

    private Rut(int number, char checkDigit)
    {
        Number = number;
        CheckDigit = checkDigit;
    }

    /// <summary>Cuerpo numérico, sin dígito verificador.</summary>
    public int Number { get; }

    /// <summary>Dígito verificador: <c>0</c> a <c>9</c> o <c>K</c>.</summary>
    public char CheckDigit { get; }

    /// <summary>Forma canónica sin puntos: <c>12345678-5</c>.</summary>
    public string Normalized => string.Create(CultureInfo.InvariantCulture, $"{Number}-{CheckDigit}");

    /// <summary>Forma habitual con puntos: <c>12.345.678-5</c>.</summary>
    public string Formatted =>
        Number.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.') + $"-{CheckDigit}";

    public override string ToString() => Normalized;

    /// <summary>
    /// Interpreta un RUT escrito con o sin puntos y guion (<c>12.345.678-5</c>, <c>12345678-5</c>, <c>123456785</c>)
    /// y comprueba su dígito verificador. Acepta la <c>K</c> en minúscula.
    /// </summary>
    public static bool TryParse(string? input, out Rut rut)
    {
        rut = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var compact = input
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

        if (compact.Length is < 2 or > 9)
        {
            return false;
        }

        var body = compact[..^1];
        var checkDigit = compact[^1];

        if (!body.All(char.IsAsciiDigit) || !(char.IsAsciiDigit(checkDigit) || checkDigit == 'K'))
        {
            return false;
        }

        var number = int.Parse(body, CultureInfo.InvariantCulture);
        if (number is < 1 or > MaxNumber || ComputeCheckDigit(number) != checkDigit)
        {
            return false;
        }

        rut = new Rut(number, checkDigit);
        return true;
    }

    /// <summary>Calcula el dígito verificador del cuerpo del RUT con el algoritmo de módulo 11.</summary>
    public static char ComputeCheckDigit(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var sum = 0;
        var weight = 2;
        for (var remaining = number; remaining > 0; remaining /= 10)
        {
            sum += remaining % 10 * weight;
            weight = weight == 7 ? 2 : weight + 1;
        }

        return (11 - (sum % 11)) switch
        {
            11 => '0',
            10 => 'K',
            var digit => (char)('0' + digit),
        };
    }
}
