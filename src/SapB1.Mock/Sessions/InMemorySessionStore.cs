using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;

namespace SapB1.Mock.Sessions;

internal sealed class InMemorySessionStore(TimeProvider time, IOptions<MockOptions> options) : ISessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastActivity = [];
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(options.Value.SessionTimeoutMinutes);

    public string Create()
    {
        var id = Guid.NewGuid().ToString();
        var now = time.GetUtcNow();

        lock (_gate)
        {
            RemoveExpired(now);
            _lastActivity[id] = now;
        }

        return id;
    }

    public bool TryTouch(string sessionId)
    {
        var now = time.GetUtcNow();

        lock (_gate)
        {
            if (!_lastActivity.TryGetValue(sessionId, out var last))
            {
                return false;
            }

            if (now - last > _timeout)
            {
                _lastActivity.Remove(sessionId);
                return false;
            }

            _lastActivity[sessionId] = now;
            return true;
        }
    }

    public bool Remove(string sessionId)
    {
        lock (_gate)
        {
            return _lastActivity.Remove(sessionId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lastActivity.Clear();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in _lastActivity.Where(s => now - s.Value > _timeout).Select(s => s.Key).ToList())
        {
            _lastActivity.Remove(expired);
        }
    }
}
