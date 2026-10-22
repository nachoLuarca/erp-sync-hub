using System.Text.Json;
using System.Text.Json.Nodes;

namespace SapB1.Mock.OData;

/// <summary>Comparación de valores JSON con la semántica básica de OData.</summary>
internal static class ODataValues
{
    public static bool TryGetNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        return node is JsonValue value
            && node.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue(out number);
    }

    public static bool TryGetString(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is JsonValue value && node.GetValueKind() == JsonValueKind.String && value.TryGetValue(out string? s))
        {
            text = s;
            return true;
        }

        return false;
    }

    private static bool TryGetBoolean(JsonNode? node, out bool flag)
    {
        flag = false;
        var kind = node?.GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False)
        {
            flag = kind == JsonValueKind.True;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compara dos valores. Devuelve <see langword="null"/> si no son comparables entre sí
    /// (tipos distintos); <c>null</c> se ordena antes que cualquier valor.
    /// </summary>
    public static int? Compare(JsonNode? left, JsonNode? right)
    {
        var leftIsNull = left is null || left.GetValueKind() == JsonValueKind.Null;
        var rightIsNull = right is null || right.GetValueKind() == JsonValueKind.Null;

        if (leftIsNull || rightIsNull)
        {
            return leftIsNull && rightIsNull ? 0 : leftIsNull ? -1 : 1;
        }

        if (TryGetNumber(left, out var leftNumber) && TryGetNumber(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (TryGetString(left, out var leftText) && TryGetString(right, out var rightText))
        {
            return string.CompareOrdinal(leftText, rightText);
        }

        if (TryGetBoolean(left, out var leftFlag) && TryGetBoolean(right, out var rightFlag))
        {
            return leftFlag.CompareTo(rightFlag);
        }

        return null;
    }
}
