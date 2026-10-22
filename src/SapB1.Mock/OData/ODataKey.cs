namespace SapB1.Mock.OData;

internal static class ODataKey
{
    /// <summary>
    /// Desescapa una clave de texto capturada de <c>Entidad('clave')</c>: en OData una comilla simple
    /// dentro del literal se escribe duplicada.
    /// </summary>
    public static string Unescape(string key) => key.Replace("''", "'", StringComparison.Ordinal);
}
