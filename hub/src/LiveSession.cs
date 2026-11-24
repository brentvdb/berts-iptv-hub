using System.Threading.Channels;

namespace BertsIptvHub;

/// <summary>One TV watching the current live stream.</summary>
public sealed class Subscriber
{
    private readonly Channel<byte[]> _queue;

    public Subscriber(LiveSession session, string who, int capacity)
    {
        Session = session;
        Who = who;
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → we drop the slow TV
        });
    }

    public LiveSession Session { get; }
    public string Who { get; }
    public DateTime SinceUtc { get; } = DateTime.UtcNow;
    public ChannelReader<byte[]> Reader => _queue.Reader;

    internal bool TryWrite(byte[] chunk) => _queue.Writer.TryWrite(chunk);
    internal void Complete() => _queue.Writer.TryComplete();
}

/// <summary>
/// A single connection to the provider for one live channel. Bytes are copied as-is to every
/// subscribed TV (no decoding/encoding). Keeps a short buffer so late joiners start instantly.
/// </summary>
public sealed class LiveSession
{
    private readonly string _url;
    private readonly HttpClient _http;
    private readonly HubOptions _o;
    private readonly ILogger _log;
    private readonly Action<LiveSession> _onFailed;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _lock = new();
    private readonly List<Subscriber> _subs = new();
    private readonly LinkedList<byte[]> _buffer = new();
    private long _bufferBytes;
    private DateTime? _emptySince = DateTime.UtcNow;
    private volatile bool _ended;

    private long _bytesIn;
    private int _reconnects;
    private DateTime? _connectedUtc;

    public LiveSession(ChannelInfo info, string url, HttpClient http, HubOptions o, ILogger log, Action<LiveSession> onFailed)
    {
        Info = info;
        _url = url;
        _http = http;
        _o = o;
        _log = log;
        _onFailed = onFailed;
    }

    public ChannelInfo Info { get; }
    public int StreamId => Info.Id;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public bool Ended => _ended;
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public int Reconnects => _reconnects;
    public DateTime? ConnectedUtc => _connectedUtc;

    public DateTime? EmptySince { get { lock (_lock) return _emptySince; } }
    public int Viewers { get { lock (_lock) return _subs.Count; } }
    public IReadOnlyList<Subscriber> SubscribersSnapshot() { lock (_lock) return _subs.ToList(); }

    public void Start() => _ = Task.Run(PumpAsync);

    /// <summary>Stop on purpose (channel switch / stop command / idle). Ends every TV's response.</summary>
    public void Stop()
    {
        if (_ended) return;
        _log.LogInformation("Stopping upstream for {Name} ({Id})", Info.Name, Info.Id);
        _cts.Cancel();
        CompleteAll();
    }

    public Subscriber AddSubscriber(string who)
    {
        var s = new Subscriber(this, who, _o.SubscriberQueueChunks);
        lock (_lock)
        {
            if (_ended)
            {
                s.Complete();
                return s;
            }
            if (_bufferBytes > 0)
            {
                var all = new byte[_bufferBytes];
                int pos = 0;
                foreach (var c in _buffer)
                {
                    Buffer.BlockCopy(c, 0, all, pos, c.Length);
                    pos += c.Length;
                }
                var prefix = TsJoin.BuildPrefix(all);
                if (prefix.Length > 0) s.TryWrite(prefix);
            }
            _subs.Add(s);
            _emptySince = null;
        }
        _log.LogInformation("{Who} joined {Name} ({Viewers} watching)", who, Info.Name, Viewers);
        return s;
    }

    public void RemoveSubscriber(Subscriber s)
    {
        bool removed;
        lock (_lock)
        {
            removed = _subs.Remove(s);
            if (removed && _subs.Count == 0) _emptySince = DateTime.UtcNow;
        }
        s.Complete();
        if (removed) _log.LogInformation("{Who} left {Name} ({Viewers} watching)", s.Who, Info.Name, Viewers);
    }

    private void Fanout(byte[] chunk)
    {
        lock (_lock)
        {
            if (_ended) return;

            _buffer.AddLast(chunk);
            _bufferBytes += chunk.Length;
            long max = Math.Max(256, _o.JoinBufferKB) * 1024L;
            while (_buffer.Count > 1 && _bufferBytes - _buffer.First!.Value.Length >= max)
            {
                _bufferBytes -= _buffer.First.Value.Length;
                _buffer.RemoveFirst();
            }

            for (int i = _subs.Count - 1; i >= 0; i--)
            {
                if (_subs[i].TryWrite(chunk)) continue;
                _log.LogWarning("{Who} fell too far behind on {Name}; disconnecting it", _subs[i].Who, Info.Name);
                _subs[i].Complete();
                _subs.RemoveAt(i);
                if (_subs.Count == 0) _emptySince = DateTime.UtcNow;
            }
        }
    }

    private void CompleteAll()
    {
        lock (_lock)
        {
            _ended = true;
            foreach (var s in _subs) s.Complete();
            _subs.Clear();
            _buffer.Clear();
            _bufferBytes = 0;
        }
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        int failures = 0;
        bool failed = false;
        var readBuf = new byte[64 * 1024];

        while (!ct.IsCancellationRequested)
        {
            DateTime? dataSince = null;
            try
            {
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                headerCts.CancelAfter(Timeout.Infinite);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"provider returned {(int)resp.StatusCode} {resp.ReasonPhrase}");

                _connectedUtc = DateTime.UtcNow;
                _log.LogInformation("Upstream connected: {Name} ({Id}) {Type}", Info.Name, Info.Id,
                    resp.Content.Headers.ContentType?.MediaType ?? "?");

                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, _o.UpstreamStallSeconds)));
                    int n;
                    try
                    {
                        n = await body.ReadAsync(readBuf, stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"no data for {_o.UpstreamStallSeconds}s");
                    }
                    if (n == 0)
                    {
                        _log.LogWarning("Upstream for {Name} ended", Info.Name);
                        break;
                    }
                    dataSince ??= DateTime.UtcNow;
                    Interlocked.Add(ref _bytesIn, n);
                    var chunk = new byte[n];
                    Buffer.BlockCopy(readBuf, 0, chunk, 0, n);
                    Fanout(chunk);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                _log.LogWarning("Upstream problem on {Name}: {Msg}", Info.Name, ex.Message);
            }

            if (ct.IsCancellationRequested) break;

            // A stream that played fine for a while earns a fresh set of retries.
            if (dataSince is DateTime since && DateTime.UtcNow - since > TimeSpan.FromSeconds(30)) failures = 0;
            failures++;
            _reconnects++;
            if (failures > _o.UpstreamRetries)
            {
                _log.LogError("Giving up on {Name} after {N} attempts", Info.Name, failures);
                failed = true;
                break;
            }

            lock (_lock) { _buffer.Clear(); _bufferBytes = 0; }
            try { await Task.Delay(Math.Min(1000 * failures, 5000), ct); }
            catch (OperationCanceledException) { break; }
            _log.LogInformation("Reconnecting to {Name} (attempt {N})", Info.Name, failures);
        }

        CompleteAll();
        if (failed) _onFailed(this);
    }
}
