namespace SapB1.Client.Internal;

/// <summary>Sesión de SAP vigente: la cabecera <c>Cookie</c> con la que se firman las peticiones.</summary>
/// <remarks>Se compara por referencia, para invalidar solo la sesión que falló y no una más nueva.</remarks>
internal sealed class SapB1Session(string cookieHeader)
{
    public string CookieHeader { get; } = cookieHeader;
}

/// <summary>
/// Estado de sesión compartido por todas las peticiones del cliente. Vive como singleton porque
/// <c>IHttpClientFactory</c> recicla los handlers y no pueden guardar la sesión.
/// </summary>
internal sealed class SapB1SessionStore
{
    private SapB1Session? _current;

    /// <summary>Serializa los logins: con varias peticiones concurrentes sin sesión, solo una inicia sesión.</summary>
    public SemaphoreSlim LoginGate { get; } = new(1, 1);

    public SapB1Session? Current => Volatile.Read(ref _current);

    public void Set(SapB1Session session) => Volatile.Write(ref _current, session);

    /// <summary>Descarta la sesión solo si sigue siendo la indicada; si otra petición ya la renovó, no hace nada.</summary>
    public void Invalidate(SapB1Session session) => Interlocked.CompareExchange(ref _current, null, session);
}
