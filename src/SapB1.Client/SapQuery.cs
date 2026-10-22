using System.Globalization;

namespace SapB1.Client;

/// <summary>Opciones de consulta OData del Service Layer para listar entidades.</summary>
/// <remarks>
/// Construya los literales de <see cref="Filter"/> con <see cref="SapFilter"/> para escapar los valores.
/// </remarks>
public sealed record SapQuery
{
    /// <summary>Expresión <c>$filter</c>, por ejemplo <c>CardType eq 'cCustomer'</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>Propiedades a traer (<c>$select</c>). Las demás quedan con su valor por defecto.</summary>
    public IReadOnlyList<string>? Select { get; init; }

    /// <summary>Orden (<c>$orderby</c>), por ejemplo <c>DocEntry desc</c>.</summary>
    public string? OrderBy { get; init; }

    /// <summary>Máximo de filas a devolver en total (<c>$top</c>), aunque abarquen varias páginas.</summary>
    public int? Top { get; init; }

    /// <summary>Filas a omitir al comienzo (<c>$skip</c>).</summary>
    public int? Skip { get; init; }

    internal string ToQueryString()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            parts.Add($"$filter={Uri.EscapeDataString(Filter)}");
        }

        if (Select is { Count: > 0 })
        {
            parts.Add($"$select={Uri.EscapeDataString(string.Join(',', Select))}");
        }

        if (!string.IsNullOrWhiteSpace(OrderBy))
        {
            parts.Add($"$orderby={Uri.EscapeDataString(OrderBy)}");
        }

        if (Top is { } top)
        {
            parts.Add($"$top={top.ToString(CultureInfo.InvariantCulture)}");
        }

        if (Skip is { } skip)
        {
            parts.Add($"$skip={skip.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
