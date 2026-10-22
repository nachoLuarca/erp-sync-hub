namespace SapB1.Client;

/// <summary>Constructores de expresiones <c>$filter</c> que escapan los valores recibidos.</summary>
public static class SapFilter
{
    /// <summary>Convierte un texto en un literal OData: entre comillas simples y duplicando las comillas internas.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary><c>Propiedad eq 'valor'</c>.</summary>
    public static string Equal(string property, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return $"{property} eq {Quote(value)}";
    }

    /// <summary>Une condiciones con <c>and</c>, cada una entre paréntesis.</summary>
    public static string And(params string[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return string.Join(" and ", conditions.Select(condition => $"({condition})"));
    }
}
