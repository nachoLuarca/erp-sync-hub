using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// Una tienda WooCommerce en memoria que imita lo que el hub usa de <c>wc/v3</c>: búsqueda de productos por SKU, lotes con su
/// límite de 100 elementos, errores por producto y autenticación Basic. Registra cada petición recibida.
/// </summary>
internal sealed class FakeWooCommerceHandler : HttpMessageHandler
{
    public const string ConsumerKey = "ck_0123456789abcdef0123456789abcdef01234567";
    public const string ConsumerSecret = "cs_0123456789abcdef0123456789abcdef01234567";
    public const int BatchLimit = 100;

    private readonly Queue<Func<HttpResponseMessage>> _scripted = new();

    public Dictionary<long, Product> Products { get; } = [];

    /// <summary>Cada petición: método, ruta con consulta y cuerpo.</summary>
    public List<(HttpMethod Method, string PathAndQuery, string Body)> Requests { get; } = [];

    public List<AuthenticationHeader> Authorizations { get; } = [];

    public IEnumerable<JsonObject> Batches =>
        Requests.Where(r => r.Method == HttpMethod.Post).Select(r => JsonNode.Parse(r.Body)!.AsObject());

    public Product AddProduct(long id, string sku, int stock = 0, string? price = null)
    {
        var product = new Product(id, sku) { StockQuantity = stock, RegularPrice = price };
        Products[id] = product;
        return product;
    }

    /// <summary>La próxima petición recibe esta respuesta en vez de la normal.</summary>
    public void RespondNextWith(HttpStatusCode status, string body = "{}") =>
        _scripted.Enqueue(() => Json(status, body));

    /// <summary>La próxima petición lanza esta excepción.</summary>
    public void FailNextWith(Exception exception) => _scripted.Enqueue(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
        Authorizations.Add(new AuthenticationHeader(request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));

        if (_scripted.TryDequeue(out var scripted))
        {
            return scripted();
        }

        if (!IsAuthorized(request))
        {
            return Json(
                HttpStatusCode.Unauthorized,
                """{"code":"woocommerce_rest_cannot_view","message":"Lo siento, no puedes listar recursos.","data":{"status":401}}""");
        }

        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products")
        {
            return Search(request.RequestUri);
        }

        if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/batch")
        {
            return Batch(body);
        }

        return Json(HttpStatusCode.NotFound, """{"code":"rest_no_route","message":"No route was found.","data":{"status":404}}""");
    }

    private static bool IsAuthorized(HttpRequestMessage request)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ConsumerKey}:{ConsumerSecret}"));
        return request.Headers.Authorization is { Scheme: "Basic" } header && header.Parameter == expected;
    }

    private HttpResponseMessage Search(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var skus = (query["sku"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var perPage = int.Parse(query["per_page"] ?? "10", System.Globalization.CultureInfo.InvariantCulture);

        var matches = Products.Values
            .Where(product => skus.Contains(product.Sku, StringComparer.OrdinalIgnoreCase))
            .Take(perPage)
            .Select(product => new JsonObject { ["id"] = product.Id, ["sku"] = product.Sku });

        return Json(HttpStatusCode.OK, new JsonArray([.. matches]).ToJsonString());
    }

    private HttpResponseMessage Batch(string body)
    {
        var update = JsonNode.Parse(body)!["update"]!.AsArray();
        if (update.Count > BatchLimit)
        {
            return Json(
                HttpStatusCode.RequestEntityTooLarge,
                $$$"""{"code":"woocommerce_rest_batch_items_limit","message":"Se permiten hasta {{{BatchLimit}}} elementos por lote.","data":{"status":413}}""");
        }

        var results = new JsonArray();
        foreach (var item in update.OfType<JsonObject>())
        {
            var id = item["id"]!.GetValue<long>();
            if (!Products.TryGetValue(id, out var product))
            {
                results.Add(new JsonObject
                {
                    ["id"] = id,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "woocommerce_rest_product_invalid_id",
                        ["message"] = "ID no válido.",
                        ["data"] = new JsonObject { ["status"] = 400 },
                    },
                });
                continue;
            }

            if (item["stock_quantity"] is { } stock)
            {
                // Como WooCommerce: sin manage_stock, la cantidad se ignora.
                if (item["manage_stock"]?.GetValue<bool>() == true)
                {
                    product.StockQuantity = stock.GetValue<int>();
                    product.ManageStock = true;
                }
            }

            if (item["regular_price"] is { } price)
            {
                product.RegularPrice = price.GetValue<string>();
            }

            results.Add(new JsonObject { ["id"] = id, ["sku"] = product.Sku });
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["update"] = results }.ToJsonString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public sealed record AuthenticationHeader(string? Scheme, string? Parameter);

    public sealed class Product(long id, string sku)
    {
        public long Id { get; } = id;

        public string Sku { get; } = sku;

        public int StockQuantity { get; set; }

        public bool ManageStock { get; set; }

        public string? RegularPrice { get; set; }
    }
}
