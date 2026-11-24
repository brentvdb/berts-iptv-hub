namespace BertsIptvHub;

/// <summary>What every TV is told about the hub's single playback slot.</summary>
public sealed record HubStateDto(
    string Type,
    string Mode,            // "idle" | "live" | "vod"
    int? StreamId,
    string? Name,
    string? Icon,
    string? EpgChannelId,
    string? VodKind,        // "movie" | "series" | "timeshift"
    string? VodId,
    DateTime? StartedAt,
    int Viewers,
    long Version,
    string? Reason);

/// <summary>A movie / series episode / catch-up being played (also uses the one provider slot).</summary>
public sealed class VodLease
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;
    private int _active;

    public VodLease(string kind, string key, string name)
    {
        Kind = kind;
        Key = key;
        Name = name;
    }

    public string Kind { get; }
    public string Key { get; }
    public string Name { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? EmptySince { get; private set; }
    public int ActiveRequests { get { lock (_lock) return _active; } }

    /// <summary>Each seek opens a new request; the previous one is cancelled so only one provider connection exists.</summary>
    public CancellationToken NewRequest()
    {
        lock (_lock)
        {
            _current?.Cancel();
            _current = new CancellationTokenSource();
            _active++;
            EmptySince = null;
            return _current.Token;
        }
    }

    public void EndRequest()
    {
        lock (_lock)
        {
            _active = Math.Max(0, _active - 1);
            if (_active == 0) EmptySince = DateTime.UtcNow;
        }
    }

    public void Cancel()
    {
        lock (_lock) _current?.Cancel();
    }
}

/// <summary>
/// Owns the single provider connection. Every TV follows whatever is playing here:
/// picking a channel on any TV switches all of them; stop on any TV stops all of them.
/// </summary>
public sealed class StreamHub : IDisposable
{
    private readonly HubOptions _o;
    private readonly ProviderClient _provider;
    private readonly HttpClient _http;
    private readonly ILoggerFactory _lf;
    private readonly ILogger<StreamHub> _log;
    private readonly Timer _timer;

    private readonly object _gate = new();
    private LiveSession? _live;
    private VodLease? _vod;
    private long _version;
    private string? _reason;

    public event Action<HubStateDto>? StateChanged;

    public StreamHub(HubOptions o, ProviderClient provider, HttpClients clients, ILoggerFactory lf)
    {
        _o = o;
        _provider = provider;
        _http = clients.Streams;
        _lf = lf;
        _log = lf.CreateLogger<StreamHub>();
        _timer = new Timer(_ => Tick(), null, 2000, 2000);
    }

    public LiveSession? CurrentLive { get { lock (_gate) return _live; } }

    // ---- live -----------------------------------------------------------------------------

    public async Task<Subscriber> SubscribeLiveAsync(int id, string who, CancellationToken ct)
    {
        var info = await LookupAsync(id, ct);
        Subscriber sub;
        lock (_gate)
        {
            if (_live is null || _live.StreamId != id || _live.Ended)
            {
                StopAllLocked(_live is null && _vod is null ? null : "switched");
                _live = StartLocked(info, who);
            }
            sub = _live.AddSubscriber(who);
        }
        Publish();
        return sub;
    }

    /// <summary>Start (pre-warm) a channel without a viewer yet, e.g. from the WebSocket "play" command.</summary>
    public async Task StartLiveAsync(int id, string who, CancellationToken ct)
    {
        var info = await LookupAsync(id, ct);
        lock (_gate)
        {
            if (_live is { Ended: false } && _live.StreamId == id) return;
            StopAllLocked("switched");
            _live = StartLocked(info, who);
        }
        Publish();
    }

    public void Unsubscribe(Subscriber s)
    {
        s.Session.RemoveSubscriber(s);
        Publish();
    }

    public void Stop(string reason)
    {
        lock (_gate)
        {
            if (_live is null && _vod is null) return;
            _log.LogInformation("Stop requested ({Reason})", reason);
            StopAllLocked(reason);
        }
        Publish();
    }

    // ---- vod / catch-up ---------------------------------------------------------------------

    public (VodLease lease, CancellationToken token) AcquireVod(string kind, string key, string name)
    {
        VodLease lease;
        CancellationToken token;
        lock (_gate)
        {
            if (_vod is null || _vod.Key != key)
            {
                StopAllLocked(_live is null && _vod is null ? null : "switched");
                _vod = new VodLease(kind, key, name);
                _reason = null;
                _log.LogInformation("Playing {Kind} {Name}", kind, name);
            }
            lease = _vod;
            token = lease.NewRequest();
        }
        Publish();
        return (lease, token);
    }

    public void ReleaseVod(VodLease lease)
    {
        lease.EndRequest();
        Publish();
    }

    // ---- state ------------------------------------------------------------------------------

    public HubStateDto Snapshot()
    {
        lock (_gate) return BuildLocked();
    }

    private HubStateDto BuildLocked()
    {
        if (_live is { Ended: false } l)
            return new HubStateDto("state", "live", l.StreamId, l.Info.Name, l.Info.Icon, l.Info.EpgChannelId,
                null, null, l.StartedUtc, l.Viewers, _version, null);
        if (_vod is { } v)
            return new HubStateDto("state", "vod", null, v.Name, null, null, v.Kind, v.Key, v.StartedUtc,
                v.ActiveRequests, _version, null);
        return new HubStateDto("state", "idle", null, null, null, null, null, null, null, 0, _version, _reason);
    }

    private void Publish()
    {
        HubStateDto dto;
        lock (_gate)
        {
            _version++;
            dto = BuildLocked();
        }
        try { StateChanged?.Invoke(dto); }
        catch (Exception ex) { _log.LogWarning("State broadcast failed: {Msg}", ex.Message); }
    }

    // ---- internals --------------------------------------------------------------------------

    private async Task<ChannelInfo> LookupAsync(int id, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        ChannelInfo? info = null;
        try { info = await _provider.GetLiveChannelAsync(id, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return info ?? new ChannelInfo(id, $"Channel {id}", null, null);
    }

    private LiveSession StartLocked(ChannelInfo info, string who)
    {
        _log.LogInformation("{Who} started {Name} ({Id})", who, info.Name, info.Id);
        var s = new LiveSession(info, _provider.LiveUrl(info.Id), _http, _o, _lf.CreateLogger<LiveSession>(), OnLiveFailed);
        _reason = null;
        s.Start();
        return s;
    }

    private void StopAllLocked(string? reason)
    {
        _live?.Stop();
        _live = null;
        _vod?.Cancel();
        _vod = null;
        if (reason != null) _reason = reason;
    }

    private void OnLiveFailed(LiveSession s)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_live == s)
            {
                _live = null;
                _reason = "upstream_failed";
                changed = true;
            }
        }
        if (changed) Publish();
    }

    private void Tick()
    {
        bool changed = false;
        var idle = TimeSpan.FromSeconds(Math.Max(5, _o.IdleTimeoutSeconds));
        lock (_gate)
        {
            if (_live?.EmptySince is DateTime t && DateTime.UtcNow - t > idle)
            {
                _log.LogInformation("Nobody watching {Name} for {S}s; closing provider connection", _live.Info.Name, idle.TotalSeconds);
                StopAllLocked("no_viewers");
                changed = true;
            }
            else if (_vod is { ActiveRequests: 0, EmptySince: DateTime v } && DateTime.UtcNow - v > idle)
            {
                _vod = null;
                _reason = "no_viewers";
                changed = true;
            }
        }
        if (changed) Publish();
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) StopAllLocked("shutdown");
    }
}
