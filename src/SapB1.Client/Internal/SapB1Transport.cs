using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SapB1.Client.Internal;

/// <summary>Peticiones HTTP al Service Layer y traducción de sus errores a excepciones tipadas.</summary>
internal sealed class SapB1Transport(HttpClient http, SapB1SessionStore store, int pageSize)
{
    /// <summary>Devuelve la entidad, o <see langword="null"/> si SAP responde que no existe.</summary>
    public async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(relativeUrl, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var (code, message) = await SapErrorReader.ReadAsync(response, cancellationToken);
            if (code == SapB1ErrorCodes.NotFound)
            {
                return null;
            }

            throw new SapB1ApiException(response.StatusCode, code, message);
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    /// <summary>Recorre la colección siguiendo <c>odata.nextLink</c> hasta agotarla o alcanzar <see cref="SapQuery.Top"/>.</summary>
    public async IAsyncEnumerable<T> QueryAsync<T>(
        string entitySet,
        SapQuery? query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var remaining = query?.Top;
        string? url = entitySet + query?.ToQueryString();

        while (url is not null && remaining is not 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={pageSize}");

            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var page = await ReadAsync<ODataPage<T>>(response, cancellationToken);

            foreach (var item in page.Value)
            {
                if (remaining is 0)
                {
                    yield break;
                }

                yield return item;
                remaining--;
            }

            url = page.Value.Count == 0 ? null : page.NextLink;
        }
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string entitySet,
        TRequest body,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(entitySet, body, SapJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    public async Task PatchAsync<TRequest>(string relativeUrl, TRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, relativeUrl)
        {
            Content = JsonContent.Create(body, options: SapJson.Options),
        };

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Cierra la sesión actual. Si no hay sesión, o SAP ya la había cerrado, no hace nada.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (store.Current is not { } session)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "Logout");
        request.Options.Set(SapB1SessionHandler.NoSessionRenewal, true);

        using var response = await http.SendAsync(request, cancellationToken);
        store.Invalidate(session);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var (code, message) = await SapErrorReader.ReadAsync(response, cancellationToken);

        // El handler ya intentó renovar la sesión: un 401 que llega hasta aquí es irrecuperable.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SapB1SessionException(
                "SAP rejected the session even after logging in again",
                response.StatusCode,
                code,
                message);
        }

        throw new SapB1ApiException(response.StatusCode, code, message);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(SapJson.Options, cancellationToken)
                ?? throw new SapB1ApiException(response.StatusCode, null, "SAP returned an empty response body");
        }
        catch (JsonException exception)
        {
            throw new SapB1ApiException(
                response.StatusCode,
                null,
                $"SAP returned a response that could not be read: {exception.Message}");
        }
    }
}
