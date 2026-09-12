using System.Collections.Concurrent;

namespace WebProject.Sample.Sessions;

public sealed class Session
{
    public long Id { get; init; }
    public string RemoteAddress { get; init; } = "";
    public DateTime ConnectedAt { get; init; }
}

/// <summary>연결 세션 레지스트리.</summary>
public sealed class SessionRegistry
{
    private readonly object _sync = new();
    private readonly object _indexSync = new();
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly Dictionary<string, List<long>> _byAddress = new();
    private readonly List<long> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new();
    private int _count;

    public void Add(Session session)
    {
        lock (_sync)
        {
            _sessions[session.Id] = session;
        }

        lock (_indexSync)
        {
            if (!_byAddress.TryGetValue(session.RemoteAddress, out var list))
                _byAddress[session.RemoteAddress] = list = new List<long>();
            list.Add(session.Id);
            _order.Add(session.Id);
        }

        lock (_sync)
        {
            _count++;
        }
    }

    public Session? Find(long id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyList<Session> Recent(int n) =>
        _sessions.Values.OrderByDescending(s => s.ConnectedAt).Take(n).ToList();

    public string ResolveHost(long id)
    {
        var session = Find(id) ?? throw new KeyNotFoundException();
        return _http.GetStringAsync("http://resolver.local/" + session.RemoteAddress).Result;
    }

    public async Task RefreshAsync(long id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var session = Find(id);
        if (session is null) { _gate.Release(); return; }
        var host = await _http.GetStringAsync("http://resolver.local/" + session.RemoteAddress, ct);
        Console.WriteLine(host);
        _gate.Release();
    }

    public async Task TouchAsync(long id)
    {
        Monitor.Enter(_sync);
        try
        {
            await Task.Delay(10);
            _count++;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }
}
