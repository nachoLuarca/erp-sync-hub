using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SapB1.Client.Internal;

/// <summary>
/// Inicia sesión al primer uso, firma cada petición con la cookie <c>B1SESSION</c> y, si SAP responde 401
/// (sesión expirada o cerrada), renueva la sesión y reintenta la petición una sola vez.
/// </summary>
/// <remarks>
/// Reintentar es seguro incluso para POST: el Service Layer valida la sesión antes de procesar la
/// petición, de modo que un 401 nunca deja una operación a medias.
/// </remarks>
internal sealed partial class SapB1SessionHandler(
    SapB1SessionStore store,
    IOptions<SapB1Options> options,
    ILogger<SapB1SessionHandler> logger) : DelegatingHandler
{
    /// <summary>Marca una petición que no debe iniciar ni renovar sesión (por ejemplo, el cierre de sesión).</summary>
    internal static readonly HttpRequestOptionsKey<bool> NoSessionRenewal = new("SapB1.NoSessionRenewal");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var renewable = !request.Options.TryGetValue(NoSessionRenewal, out var skip) || !skip;
        var snapshot = await RequestSnapshot.CaptureAsync(request, cancellationToken);

        var session = renewable ? await EnsureSessionAsync(cancellationToken) : store.Current;
        var response = await base.SendAsync(snapshot.ToRequest(session), cancellationToken);

        if (!renewable || response.StatusCode != HttpStatusCode.Unauthorized || session is null)
        {
            return response;
        }

        response.Dispose();
        store.Invalidate(session);
        LogSessionRejected();

        var renewed = await EnsureSessionAsync(cancellationToken);
        return await base.SendAsync(snapshot.ToRequest(renewed), cancellationToken);
    }

    private async Task<SapB1Session> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (store.Current is { } existing)
        {
            return existing;
        }

        await store.LoginGate.WaitAsync(cancellationToken);
        try
        {
            // Otra petición pudo iniciar sesión mientras esperábamos el turno.
            if (store.Current is { } created)
            {
                return created;
            }

            var session = await LoginAsync(cancellationToken);
            store.Set(session);
            return session;
        }
        finally
        {
            store.LoginGate.Release();
        }
    }

    private async Task<SapB1Session> LoginAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var loginUri = new Uri(settings.ServiceLayerUri, "Login");

        using var request = new HttpRequestMessage(HttpMethod.Post, loginUri)
        {
            Content = JsonContent.Create(
                new LoginRequest(settings.CompanyDb, settings.UserName, settings.Password),
                options: SapJson.Options),
        };

        using var response = await base.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (code, message) = await SapErrorReader.ReadAsync(response, cancellationToken);
            throw new SapB1SessionException("SAP login failed", response.StatusCode, code, message);
        }

        LoginResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<LoginResponse>(SapJson.Options, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new SapB1SessionException(
                $"SAP login returned a response that is not valid JSON: {exception.Message}",
                response.StatusCode,
                sapErrorCode: null,
                sapErrorMessage: null);
        }

        var cookies = ReadCookies(response);
        if (!cookies.Exists(cookie => cookie.StartsWith("B1SESSION=", StringComparison.Ordinal)))
        {
            if (string.IsNullOrEmpty(body?.SessionId))
            {
                throw new SapB1SessionException(
                    "SAP login succeeded but returned no session",
                    response.StatusCode,
                    sapErrorCode: null,
                    sapErrorMessage: null);
            }

            cookies.Insert(0, $"B1SESSION={body.SessionId}");
        }

        LogLoggedIn(body?.SessionTimeout);
        return new SapB1Session(string.Join("; ", cookies));
    }

    /// <summary>Extrae los pares <c>nombre=valor</c> de las cabeceras <c>Set-Cookie</c> (B1SESSION y ROUTEID).</summary>
    private static List<string> ReadCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return [];
        }

        return [.. values.Select(value => value.Split(';', 2)[0].Trim()).Where(pair => pair.Contains('=', StringComparison.Ordinal))];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in to the SAP Service Layer (session timeout: {SessionTimeoutMinutes} min)")]
    private partial void LogLoggedIn(int? sessionTimeoutMinutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "SAP rejected the session (expired or closed); logging in again")]
    private partial void LogSessionRejected();

    /// <summary>Copia reutilizable de una petición, para poder reenviarla con otra sesión.</summary>
    private sealed class RequestSnapshot
    {
        private readonly HttpMethod _method;
        private readonly Uri? _uri;
        private readonly Version _version;
        private readonly HttpVersionPolicy _versionPolicy;
        private readonly List<KeyValuePair<string, string[]>> _headers;
        private readonly byte[]? _body;
        private readonly List<KeyValuePair<string, string[]>> _contentHeaders;
        private readonly List<KeyValuePair<string, object?>> _options;

        private RequestSnapshot(HttpRequestMessage request, byte[]? body, List<KeyValuePair<string, string[]>> contentHeaders)
        {
            _method = request.Method;
            _uri = request.RequestUri;
            _version = request.Version;
            _versionPolicy = request.VersionPolicy;
            _headers = [.. request.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, [.. h.Value]))];
            _body = body;
            _contentHeaders = contentHeaders;
            _options = [.. ((IDictionary<string, object?>)request.Options)];
        }

        public static async Task<RequestSnapshot> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[]? body = null;
            List<KeyValuePair<string, string[]>> contentHeaders = [];

            if (request.Content is { } content)
            {
                body = await content.ReadAsByteArrayAsync(cancellationToken);
                contentHeaders = [.. content.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, [.. h.Value]))];
            }

            return new RequestSnapshot(request, body, contentHeaders);
        }

        public HttpRequestMessage ToRequest(SapB1Session? session)
        {
            var request = new HttpRequestMessage(_method, _uri)
            {
                Version = _version,
                VersionPolicy = _versionPolicy,
            };

            foreach (var header in _headers.Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (session is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            }

            foreach (var option in _options)
            {
                ((IDictionary<string, object?>)request.Options)[option.Key] = option.Value;
            }

            if (_body is not null)
            {
                request.Content = new ByteArrayContent(_body);
                foreach (var header in _contentHeaders.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return request;
        }
    }
}
