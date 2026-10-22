namespace SapB1.Client.Internal;

internal static class SapKey
{
    /// <summary>Segmento de entidad con clave de texto: <c>Items('A001')</c>, con las comillas y los caracteres especiales escapados.</summary>
    public static string Text(string entitySet, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var escaped = key.Replace("'", "''", StringComparison.Ordinal);
        return $"{entitySet}('{Uri.EscapeDataString(escaped)}')";
    }

    /// <summary>Segmento de entidad con clave numérica: <c>Orders(12)</c>.</summary>
    public static string Number(string entitySet, int key) =>
        $"{entitySet}({key.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
}
