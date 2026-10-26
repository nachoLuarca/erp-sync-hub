using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErpSyncHub.Application.Store;
using ErpSyncHub.Domain.Common;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Store;

/// <summary>
/// Adaptador de <see cref="IStoreGateway"/> sobre la API REST de WooCommerce (<c>wc/v3</c>). Busca los productos por SKU y
/// los actualiza con el endpoint <c>products/batch</c>, en lotes que respetan el límite de la tienda. Traduce los fallos a
/// <see cref="Result"/>: credenciales rechazadas y errores 4xx son definitivos; 5xx, red y tiempos agotados son pasajeros.
/// </summary>
internal sealed class WooCommerceGateway(HttpClient http, IOptions<WooCommerceOptions> options) : IStoreGateway
{
    private const string ProductsPath = "wp-json/wc/v3/products";
    private const int SearchPageSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.Value.IsConfigured;

    private readonly int _batchSize = Math.Clamp(options.Value.BatchSize, 1, WooCommerceOptions.MaxBatchSize);

    public async Task<Result<IReadOnlyDictionary<string, StoreProduct>>> FindProductsBySkuAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skus);

        // El parámetro sku de WooCommerce es una lista separada por comas: un SKU con coma no se puede buscar.
        var wanted = skus
            .Where(sku => !string.IsNullOrWhiteSpace(sku) && !sku.Contains(',', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // La tienda compara el SKU sin distinguir mayúsculas.
        var found = new Dictionary<string, StoreProduct>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in wanted.Chunk(SearchPageSize))
        {
            var query = $"{ProductsPath}?sku={Uri.EscapeDataString(string.Join(',', chunk))}&per_page={SearchPageSize}&_fields=id,sku";
            var response = await SendAsync(HttpMethod.Get, query, body: null, cancellationToken);
            if (response.IsFailure)
            {
                return Result.Failure<IReadOnlyDictionary<string, StoreProduct>>(response.Error!);
            }

            if (response.Value is not JsonArray products)
            {
                return Result.Failure<IReadOnlyDictionary<string, StoreProduct>>(
                    ResultError.Transient(StoreErrors.InvalidResponse, "La tienda no devolvió una lista de productos."));
            }

            foreach (var product in products.OfType<JsonObject>())
            {
                if (product["id"]?.GetValue<long>() is { } id && product["sku"]?.GetValue<string>() is { } sku)
                {
                    found[sku] = new StoreProduct(id, sku);
                }
            }
        }

        // El resultado se indexa por el SKU que pidió el hub, aunque la tienda lo tenga con otras mayúsculas.
        var byRequested = new Dictionary<string, StoreProduct>(StringComparer.Ordinal);
        foreach (var sku in skus)
        {
            if (found.TryGetValue(sku, out var product))
            {
                byRequested[sku] = product;
            }
        }

        return Result.Success<IReadOnlyDictionary<string, StoreProduct>>(byRequested);
    }

    public async Task<Result<IReadOnlyList<StoreUpdateOutcome>>> UpdateProductsAsync(
        IReadOnlyCollection<StoreProductUpdate> updates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var outcomes = new List<StoreUpdateOutcome>(updates.Count);
        ResultError? interruption = null;

        foreach (var batch in updates.Chunk(_batchSize))
        {
            // Tras una falla pasajera no se insiste con los lotes siguientes: la tienda no está respondiendo.
            if (interruption is not null)
            {
                outcomes.AddRange(batch.Select(update => new StoreUpdateOutcome(update.Sku, interruption)));
                continue;
            }

            var response = await SendAsync(HttpMethod.Post, $"{ProductsPath}/batch", BuildBatch(batch), cancellationToken);
            if (response.IsFailure)
            {
                // Credenciales rechazadas afectan a toda la operación; cualquier otra falla afecta solo a este lote.
                if (response.Error!.Code == StoreErrors.Unauthorized)
                {
                    return Result.Failure<IReadOnlyList<StoreUpdateOutcome>>(response.Error);
                }

                outcomes.AddRange(batch.Select(update => new StoreUpdateOutcome(update.Sku, response.Error)));
                if (response.Error.IsTransient)
                {
                    interruption = response.Error;
                }

                continue;
            }

            outcomes.AddRange(ReadBatchResults(batch, response.Value));
        }

        return Result.Success<IReadOnlyList<StoreUpdateOutcome>>(outcomes);
    }

    private static JsonObject BuildBatch(IEnumerable<StoreProductUpdate> batch)
    {
        var update = new JsonArray();
        foreach (var item in batch)
        {
            var product = new JsonObject { ["id"] = item.ProductId };

            if (item.StockQuantity is { } stock)
            {
                // Sin manage_stock, WooCommerce ignora stock_quantity.
                product["manage_stock"] = true;
                product["stock_quantity"] = stock;
            }

            if (item.Price is { } price)
            {
                // WooCommerce recibe los precios como texto.
                product["regular_price"] = price.ToString(CultureInfo.InvariantCulture);
            }

            update.Add(product);
        }

        return new JsonObject { ["update"] = update };
    }

    private static IEnumerable<StoreUpdateOutcome> ReadBatchResults(StoreProductUpdate[] batch, JsonNode? response)
    {
        var results = (response as JsonObject)?["update"] as JsonArray;
        var byId = new Dictionary<long, JsonObject>();
        foreach (var result in results?.OfType<JsonObject>() ?? [])
        {
            if (result["id"]?.GetValue<long>() is { } id)
            {
                byId[id] = result;
            }
        }

        foreach (var item in batch)
        {
            if (!byId.TryGetValue(item.ProductId, out var result))
            {
                yield return new StoreUpdateOutcome(
                    item.Sku,
                    ResultError.Transient(StoreErrors.InvalidResponse, $"La tienda no informó el resultado del producto {item.ProductId}."));
            }
            else if (result["error"] is JsonObject error)
            {
                yield return new StoreUpdateOutcome(item.Sku, TranslateItemError(error));
            }
            else
            {
                yield return new StoreUpdateOutcome(item.Sku, null);
            }
        }
    }

    private static ResultError TranslateItemError(JsonObject error)
    {
        var code = error["code"]?.GetValue<string>() ?? "desconocido";
        var message = error["message"]?.GetValue<string>() ?? "Sin detalle.";
        var status = error["data"]?["status"]?.GetValue<int>() ?? 400;
        var detail = $"La tienda rechazó el producto ({code}): {message}";

        return status >= 500
            ? ResultError.Transient(StoreErrors.ServerError, detail)
            : ResultError.Rejected(StoreErrors.Rejected, detail);
    }

    private async Task<Result<JsonNode?>> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<JsonNode?>(TranslateHttpError(response.StatusCode, text));
            }

            try
            {
                return Result.Success(JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                // Una respuesta exitosa que no es JSON suele ser la página de un proxy o un error de PHP.
                return Result.Failure<JsonNode?>(ResultError.Transient(StoreErrors.InvalidResponse, "La tienda respondió algo que no es JSON."));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // Un IOException suelto es una conexión cortada a mitad de la respuesta: igual de pasajero que no poder conectar.
            var detail = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
            return Result.Failure<JsonNode?>(ResultError.Transient(StoreErrors.Unreachable, $"No se pudo conectar con la tienda: {detail}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El HttpClient agotó su tiempo de espera; no fue una cancelación pedida por el servicio.
            return Result.Failure<JsonNode?>(ResultError.Transient(StoreErrors.Timeout, "La tienda no respondió dentro del tiempo de espera."));
        }
    }

    private static ResultError TranslateHttpError(HttpStatusCode status, string body)
    {
        var detail = DescribeBody(body);
        var number = (int)status;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ResultError.Rejected(
                StoreErrors.Unauthorized,
                $"La tienda rechazó las credenciales de la API (HTTP {number}){detail}. Revisa Store__WooCommerce__ConsumerKey y ConsumerSecret, y que la clave tenga permiso de lectura y escritura.");
        }

        // 5xx, 408 (tiempo agotado), 425 (muy pronto) y 429 (demasiadas solicitudes) se reintentan; el resto de los 4xx no.
        var transient = number is >= 500 or 408 or 425 or 429;
        return transient
            ? ResultError.Transient(StoreErrors.ServerError, $"La tienda no pudo atender la solicitud (HTTP {number}){detail}")
            : ResultError.Rejected(StoreErrors.Rejected, $"La tienda rechazó la solicitud (HTTP {number}){detail}");
    }

    /// <summary>Extrae el mensaje de error de WooCommerce (<c>{"code":..,"message":..}</c>) si el cuerpo lo trae.</summary>
    private static string DescribeBody(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject error && error["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return $": {message}";
            }
        }
        catch (JsonException)
        {
            // El cuerpo no es JSON: no hay mensaje que mostrar.
        }

        return string.Empty;
    }
}
