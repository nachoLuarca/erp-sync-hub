using SapB1.Mock.Admin;

namespace SapB1.Mock.Faults;

/// <summary>Estado de las fallas activas y decisiones de cuándo inyectarlas. Es seguro entre hilos.</summary>
internal interface IFaultInjector
{
    FaultSettings Current { get; }

    void Set(FaultSettings settings);

    void Clear();

    FaultReport Report();

    /// <summary>Contabiliza una petición del Service Layer y decide si debe responder con un error intermitente.</summary>
    bool ShouldFailRequest(out int statusCode);

    /// <summary>Contabiliza una petición autenticada y decide si su sesión debe aparecer expirada.</summary>
    bool ShouldExpireSession();

    void RecordDelay();

    void RecordOutage();
}

internal sealed class FaultInjector : IFaultInjector, IResettable
{
    private readonly Lock _gate = new();
    private FaultSettings _settings = new();
    private long _requests;
    private long _authenticatedRequests;
    private long _delayed;
    private long _outages;
    private long _errors;
    private long _sessionExpiries;

    public FaultSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public void Set(FaultSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _settings = settings;
            ResetCounters();
        }
    }

    public void Clear() => Set(new FaultSettings());

    public void Reset() => Clear();

    public FaultReport Report()
    {
        lock (_gate)
        {
            return new FaultReport(_settings, new FaultCounters(_delayed, _outages, _errors, _sessionExpiries));
        }
    }

    public bool ShouldFailRequest(out int statusCode)
    {
        lock (_gate)
        {
            statusCode = _settings.ErrorStatusCode;
            var number = ++_requests;

            var fail = (_settings.ErrorEveryNth > 0 && number % _settings.ErrorEveryNth == 0)
                || (_settings.ErrorRate > 0 && Random.Shared.NextDouble() < _settings.ErrorRate);
            if (fail)
            {
                _errors++;
            }

            return fail;
        }
    }

    public bool ShouldExpireSession()
    {
        lock (_gate)
        {
            var number = ++_authenticatedRequests;
            var expire = _settings.SessionExpiryEveryNth > 0 && number % _settings.SessionExpiryEveryNth == 0;
            if (expire)
            {
                _sessionExpiries++;
            }

            return expire;
        }
    }

    public void RecordDelay()
    {
        lock (_gate)
        {
            _delayed++;
        }
    }

    public void RecordOutage()
    {
        lock (_gate)
        {
            _outages++;
        }
    }

    private void ResetCounters()
    {
        _requests = 0;
        _authenticatedRequests = 0;
        _delayed = 0;
        _outages = 0;
        _errors = 0;
        _sessionExpiries = 0;
    }
}
