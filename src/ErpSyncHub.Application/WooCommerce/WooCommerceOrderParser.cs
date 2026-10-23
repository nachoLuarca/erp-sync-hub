using System.Globalization;
using System.Text.Json;
using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.Application.WooCommerce;

/// <summary>
/// Lee el JSON de un pedido de WooCommerce. Es tolerante con las variaciones de la API (los montos llegan como texto
/// o como número, los campos opcionales pueden faltar) y estricto con lo que el hub no puede inventar.
/// </summary>
public static class WooCommerceOrderParser
{
    public static Result<WooCommerceOrder> Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return Invalid("El cuerpo del pedido no es un JSON válido.");
        }
    }

    public static Result<WooCommerceOrder> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static Result<WooCommerceOrder> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("El cuerpo del pedido debe ser un objeto JSON.");
        }

        var id = GetLong(root, "id");
        if (id <= 0)
        {
            return Invalid("El pedido no trae un 'id' válido.");
        }

        var status = GetString(root, "status").ToLowerInvariant();
        if (status.Length == 0)
        {
            return Invalid("El pedido no trae 'status'.");
        }

        if (!root.TryGetProperty("line_items", out var lines) || lines.ValueKind != JsonValueKind.Array)
        {
            return Invalid("El pedido no trae 'line_items'.");
        }

        var lineItems = new List<WooCommerceLineItem>();
        foreach (var line in lines.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.Object))
        {
            lineItems.Add(new WooCommerceLineItem(
                GetLong(line, "id"),
                GetLong(line, "product_id"),
                GetLong(line, "variation_id"),
                GetString(line, "name"),
                GetString(line, "sku"),
                GetDecimal(line, "quantity"),
                GetDecimal(line, "subtotal"),
                GetDecimal(line, "total"),
                GetDecimal(line, "total_tax")));
        }

        var number = GetString(root, "number");

        return Result.Success(new WooCommerceOrder(
            id,
            number.Length > 0 ? number : id.ToString(CultureInfo.InvariantCulture),
            status,
            GetString(root, "currency"),
            GetDecimal(root, "total"),
            GetDecimal(root, "total_tax"),
            GetDecimal(root, "shipping_total"),
            GetDecimal(root, "discount_total"),
            GetLong(root, "customer_id"),
            NullIfEmpty(GetString(root, "customer_note")),
            GetDate(root, "date_created_gmt"),
            ReadAddress(root, "billing"),
            ReadAddress(root, "shipping"),
            lineItems,
            ReadMeta(root)));
    }

    private static WooCommerceAddress ReadAddress(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var address) || address.ValueKind != JsonValueKind.Object)
        {
            address = default;
        }

        return new WooCommerceAddress(
            GetString(address, "first_name"),
            GetString(address, "last_name"),
            GetString(address, "company"),
            GetString(address, "address_1"),
            GetString(address, "address_2"),
            GetString(address, "city"),
            GetString(address, "state"),
            GetString(address, "postcode"),
            GetString(address, "country"),
            GetString(address, "email"),
            GetString(address, "phone"));
    }

    private static Dictionary<string, string> ReadMeta(JsonElement root)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("meta_data", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return meta;
        }

        foreach (var entry in entries.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
        {
            var key = GetString(entry, "key");
            if (key.Length == 0 || meta.ContainsKey(key) || !entry.TryGetProperty("value", out var value))
            {
                continue;
            }

            meta[key] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
        }

        return meta;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0,
        };
    }

    /// <summary>WooCommerce envía los montos como texto ("29990.00") en unos campos y como número en otros.</summary>
    private static decimal GetDecimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0,
        };
    }

    /// <summary>Las fechas "_gmt" vienen sin zona (<c>2026-03-10T15:04:05</c>) y están en UTC.</summary>
    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static Result<WooCommerceOrder> Invalid(string message) =>
        Result.Failure<WooCommerceOrder>(ResultError.Validation("woocommerce.invalid_order", message));
}
