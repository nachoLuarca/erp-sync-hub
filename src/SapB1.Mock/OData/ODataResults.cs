using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.OData;

/// <summary>
/// Respuestas de colección y de entidad con el envoltorio OData v3 del Service Layer v1
/// (<c>odata.metadata</c>, <c>value</c>, <c>odata.nextLink</c>, <c>odata.count</c>) y las opciones
/// de consulta <c>$select</c>, <c>$filter</c>, <c>$orderby</c>, <c>$top</c>, <c>$skip</c> e <c>$inlinecount</c>.
/// La paginación sigue al Service Layer: página por defecto configurable y <c>Prefer: odata.maxpagesize</c>.
/// </summary>
internal static partial class ODataResults
{
    public static IResult Collection(
        HttpContext http,
        string entitySet,
        IReadOnlyList<JsonObject> rows,
        int defaultPageSize)
    {
        try
        {
            return BuildCollection(http, entitySet, rows, defaultPageSize);
        }
        catch (ODataQueryException exception)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.InvalidRequest, exception.Message);
        }
    }

    public static IResult Entity(HttpContext http, string entitySet, JsonObject row, int statusCode = StatusCodes.Status200OK)
    {
        try
        {
            var known = KnownProperties([row]);
            var projected = ApplySelect(http.Request.Query, known, [row])[0];

            var body = new JsonObject { ["odata.metadata"] = $"{MetadataBase(http)}#{entitySet}/@Element" };
            foreach (var property in projected)
            {
                body[property.Key] = property.Value?.DeepClone();
            }

            return SapResults.Json(body, statusCode);
        }
        catch (ODataQueryException exception)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.InvalidRequest, exception.Message);
        }
    }

    private static SapJsonResult BuildCollection(
        HttpContext http,
        string entitySet,
        IReadOnlyList<JsonObject> rows,
        int defaultPageSize)
    {
        var query = http.Request.Query;
        var known = KnownProperties(rows);

        IEnumerable<JsonObject> result = rows;

        if (query.TryGetValue("$filter", out var filterValues) && !string.IsNullOrWhiteSpace(filterValues[0]))
        {
            var predicate = ODataFilterParser.Parse(filterValues[0]!, known);
            result = result.Where(predicate);
        }

        result = ApplyOrderBy(query, known, result);

        var matching = result.ToList();
        var skip = ParseNonNegative(query, "$skip") ?? 0;
        var top = ParseNonNegative(query, "$top");
        var pageSize = ResolvePageSize(http, defaultPageSize);

        var window = matching.Skip(skip);
        if (top is not null)
        {
            window = window.Take(top.Value);
        }

        var windowRows = window.ToList();
        var pageRows = pageSize > 0 ? windowRows.Take(pageSize).ToList() : windowRows;
        var hasNextPage = pageRows.Count < windowRows.Count;

        var body = new JsonObject { ["odata.metadata"] = $"{MetadataBase(http)}#{entitySet}" };

        if (query.TryGetValue("$inlinecount", out var inlineCount) && inlineCount[0] == "allpages")
        {
            body["odata.count"] = matching.Count.ToString(CultureInfo.InvariantCulture);
        }

        var projected = ApplySelect(query, known, pageRows);
        body["value"] = new JsonArray(projected.Select(row => (JsonNode?)row).ToArray());

        if (hasNextPage)
        {
            body["odata.nextLink"] = BuildNextLink(entitySet, query, skip + pageRows.Count, top is null ? null : top - pageRows.Count);
        }

        return new SapJsonResult(body, preferenceApplied: PreferredPageSize(http));
    }

    private static HashSet<string> KnownProperties(IReadOnlyList<JsonObject> rows) =>
        rows.Count == 0 ? new HashSet<string>() : rows[0].Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<JsonObject> ApplyOrderBy(
        IQueryCollection query,
        HashSet<string> known,
        IEnumerable<JsonObject> rows)
    {
        if (!query.TryGetValue("$orderby", out var values) || string.IsNullOrWhiteSpace(values[0]))
        {
            return rows;
        }

        IOrderedEnumerable<JsonObject>? ordered = null;

        foreach (var clause in values[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var property = parts[0];
            var descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.Ordinal);

            if (parts.Length > 2 || (parts.Length == 2 && !descending && !parts[1].Equals("asc", StringComparison.Ordinal)))
            {
                throw new ODataQueryException($"Invalid $orderby clause '{clause}'.");
            }

            if (known.Count > 0 && !known.Contains(property))
            {
                throw new ODataQueryException($"Property '{property}' is invalid.");
            }

            var comparer = Comparer<JsonNode?>.Create((a, b) => ODataValues.Compare(a, b) ?? 0);
            JsonNode? Key(JsonObject row) => row[property];

            ordered = (ordered, descending) switch
            {
                (null, false) => rows.OrderBy(Key, comparer),
                (null, true) => rows.OrderByDescending(Key, comparer),
                (_, false) => ordered!.ThenBy(Key, comparer),
                (_, true) => ordered!.ThenByDescending(Key, comparer),
            };
        }

        return ordered ?? rows;
    }

    private static List<JsonObject> ApplySelect(
        IQueryCollection query,
        HashSet<string> known,
        IReadOnlyList<JsonObject> rows)
    {
        if (!query.TryGetValue("$select", out var values) || string.IsNullOrWhiteSpace(values[0]))
        {
            return [.. rows];
        }

        var selected = values[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var property in selected)
        {
            if (known.Count > 0 && !known.Contains(property))
            {
                throw new ODataQueryException($"Property '{property}' is invalid.");
            }
        }

        var wanted = selected.ToHashSet(StringComparer.Ordinal);
        return rows
            .Select(row =>
            {
                var projection = new JsonObject();
                foreach (var property in row.Where(p => wanted.Contains(p.Key)))
                {
                    projection[property.Key] = property.Value?.DeepClone();
                }

                return projection;
            })
            .ToList();
    }

    private static int? ParseNonNegative(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values) || string.IsNullOrWhiteSpace(values[0]))
        {
            return null;
        }

        if (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new ODataQueryException($"Invalid value '{values[0]}' for {name}.");
        }

        return number;
    }

    /// <summary>Tamaño de página efectivo; 0 significa sin paginación.</summary>
    private static int ResolvePageSize(HttpContext http, int defaultPageSize) =>
        PreferredPageSize(http) ?? defaultPageSize;

    private static int? PreferredPageSize(HttpContext http)
    {
        foreach (var header in http.Request.Headers["Prefer"])
        {
            var match = MaxPageSizePattern().Match(header ?? string.Empty);
            if (match.Success)
            {
                return int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string BuildNextLink(string entitySet, IQueryCollection query, int skip, int? top)
    {
        var parameters = query
            .Where(p => p.Key is not ("$skip" or "$top"))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value.ToString())}")
            .ToList();

        parameters.Add($"$skip={skip.ToString(CultureInfo.InvariantCulture)}");
        if (top is not null)
        {
            parameters.Add($"$top={top.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"{entitySet}?{string.Join('&', parameters)}";
    }

    private static string MetadataBase(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}/b1s/v1/$metadata";

    [GeneratedRegex(@"odata\.maxpagesize\s*=\s*(?<size>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaxPageSizePattern();

    /// <summary>Respuesta JSON del Service Layer que informa el tamaño de página aplicado.</summary>
    private sealed class SapJsonResult(JsonObject body, int? preferenceApplied) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (preferenceApplied is not null)
            {
                httpContext.Response.Headers["Preference-Applied"] =
                    $"odata.maxpagesize={preferenceApplied.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            return SapResults.Json(body).ExecuteAsync(httpContext);
        }
    }
}
