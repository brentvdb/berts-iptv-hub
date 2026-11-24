using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BertsIptvHub;

public sealed record ChannelInfo(int Id, string Name, string? Icon, string? EpgChannelId);

/// <summary>
/// Talks to the Xtream provider for everything except live/VOD video, and caches every
/// response on disk so the TVs get instant channel lists/EPG and the provider sees very few calls.
/// </summary>
public sealed class ProviderClient
{
    public sealed record CacheEntry(string Path, DateTime FetchedUtc, long Size);

    private readonly HubOptions _o;
    private readonly HttpClient _http;
    private readonly ILogger<ProviderClient> _log;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private readonly object _indexLock = new();
    private Dictionary<int, ChannelInfo> _liveIndex = new();
    private string? _liveIndexPath;

    public ProviderClient(HubOptions o, HttpClients clients, ILogger<ProviderClient> log)
    {
        _o = o;
        _http = clients.Api;
        _log = log;
        _dir = Path.Combine(o.DataDirFull, "cache");
        Directory.CreateDirectory(_dir);
        IndexDisk();
    }

    public string BaseUrl => _o.Provider.Url.TrimEnd('/');
    public string User => _o.Provider.Username;
    public string Pass => _o.Provider.Password;

    public TimeSpan CatalogTtl => TimeSpan.FromHours(_o.CatalogCacheHours);
    public TimeSpan EpgTtl => TimeSpan.FromHours(_o.EpgCacheHours);
    public TimeSpan ShortEpgTtl => TimeSpan.FromMinutes(_o.ShortEpgCacheMinutes);

    public string Esc(string s) => Uri.EscapeDataString(s);

    public string LiveUrl(int id) => $"{BaseUrl}/live/{Esc(User)}/{Esc(Pass)}/{id}.{_o.Provider.LiveExtension}";

    public string BuildUrl(string path, IEnumerable<KeyValuePair<string, string>> extra)
    {
        var sb = new StringBuilder(BaseUrl).Append('/').Append(path)
            .Append("?username=").Append(Esc(User))
            .Append("&password=").Append(Esc(Pass));
        foreach (var kv in extra)
            sb.Append('&').Append(Esc(kv.Key)).Append('=').Append(Esc(kv.Value));
        return sb.ToString();
    }

    private static string Hash(string path, IEnumerable<KeyValuePair<string, string>> extra)
    {
        var key = path + "?" + string.Join("&", extra
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => k.Key + "=" + k.Value));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a cached file for the request. If the copy is older than ttl, the old copy is returned
    /// immediately and a refresh runs in the background (TVs never wait on the provider once anything
    /// is cached). Only the very first request for something waits for the provider.
    /// </summary>
    public async Task<CacheEntry> GetCachedAsync(string path, IReadOnlyCollection<KeyValuePair<string, string>> extra,
        TimeSpan ttl, CancellationToken ct)
    {
        var hash = Hash(path, extra);
        if (_entries.TryGetValue(hash, out var existing) && File.Exists(existing.Path))
        {
            if (DateTime.UtcNow - existing.FetchedUtc >= ttl)
                _ = RefreshInBackgroundAsync(hash, path, extra);
            return existing;
        }

        var gate = _locks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(hash, out existing) && File.Exists(existing.Path)) return existing;
            return await FetchAsync(hash, BuildUrl(path, extra), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forces a refresh now (used by the periodic updater); waits for it to finish.</summary>
    public async Task RefreshAsync(string path, IReadOnlyCollection<KeyValuePair<string, string>> extra, TimeSpan ttl, CancellationToken ct)
    {
        var hash = Hash(path, extra);
        if (_entries.TryGetValue(hash, out var e) && File.Exists(e.Path) && DateTime.UtcNow - e.FetchedUtc < ttl) return;
        var gate = _locks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(hash, out e) && File.Exists(e.Path) && DateTime.UtcNow - e.FetchedUtc < ttl) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entry = await FetchAsync(hash, BuildUrl(path, extra), ct);
            _log.LogInformation("Updated {What} from provider ({KB:N0} KB in {S:N1}s)", Describe(path, extra), entry.Size / 1024, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>When a cached item was last fetched, without touching the provider.</summary>
    public DateTime? LastUpdated(string path, params (string k, string v)[] extra)
    {
        var hash = Hash(path, extra.Select(m => new KeyValuePair<string, string>(m.k, m.v)));
        return _entries.TryGetValue(hash, out var e) ? e.FetchedUtc : null;
    }

    private async Task RefreshInBackgroundAsync(string hash, string path, IReadOnlyCollection<KeyValuePair<string, string>> extra)
    {
        var gate = _locks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) return; // already refreshing
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await FetchAsync(hash, BuildUrl(path, extra), cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Background refresh of {What} failed ({Msg}); still serving the copy from {When:u}",
                Describe(path, extra), ex.Message, _entries.TryGetValue(hash, out var e) ? e.FetchedUtc : DateTime.MinValue);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<CacheEntry> GetXmltvAsync(CancellationToken ct) =>
        GetCachedAsync("xmltv.php", Array.Empty<KeyValuePair<string, string>>(), EpgTtl, ct);

    public Task<CacheEntry> GetApiAsync(string action, CancellationToken ct, params (string k, string v)[] more)
    {
        var list = new List<KeyValuePair<string, string>> { new("action", action) };
        list.AddRange(more.Select(m => new KeyValuePair<string, string>(m.k, m.v)));
        return GetCachedAsync("player_api.php", list, CatalogTtl, ct);
    }

    public IEnumerable<object> CacheSummary() =>
        _entries.Values.OrderByDescending(e => e.FetchedUtc)
            .Select(e => new { file = Path.GetFileName(e.Path), fetched = e.FetchedUtc, bytes = e.Size });

    /// <summary>All live channels by stream id (from the cached channel list).</summary>
    public async Task<IReadOnlyDictionary<int, ChannelInfo>> GetLiveIndexAsync(CancellationToken ct)
    {
        var entry = await GetApiAsync("get_live_streams", ct);
        lock (_indexLock)
        {
            if (_liveIndexPath != entry.Path)
            {
                _liveIndex = ParseLiveIndex(entry.Path);
                _liveIndexPath = entry.Path;
            }
            return _liveIndex;
        }
    }

    /// <summary>Name/logo lookup used to tell every TV what is playing.</summary>
    public async Task<ChannelInfo?> GetLiveChannelAsync(int id, CancellationToken ct)
    {
        try
        {
            var entry = await GetApiAsync("get_live_streams", ct);
            lock (_indexLock)
            {
                if (_liveIndexPath != entry.Path)
                {
                    _liveIndex = ParseLiveIndex(entry.Path);
                    _liveIndexPath = entry.Path;
                }
                return _liveIndex.TryGetValue(id, out var c) ? c : null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("Channel lookup failed: {Msg}", ex.Message);
            return null;
        }
    }

    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    // ---------------------------------------------------------------------------------------

    private async Task<CacheEntry> FetchAsync(string hash, string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Provider returned {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var target = Path.Combine(_dir, $"{hash}_{DateTime.UtcNow.Ticks}.dat");
        var tmp = target + ".tmp";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await src.CopyToAsync(dst, ct);
        }

        var size = new FileInfo(tmp).Length;
        if (size == 0)
        {
            File.Delete(tmp);
            throw new HttpRequestException("Provider returned an empty response");
        }
        File.Move(tmp, target, overwrite: true);

        var entry = new CacheEntry(target, DateTime.UtcNow, size);
        if (_entries.TryGetValue(hash, out var old)) TryDelete(old.Path);
        _entries[hash] = entry;
        return entry;
    }

    private void IndexDisk()
    {
        foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp")) TryDelete(tmp);
        var groups = Directory.EnumerateFiles(_dir, "*.dat")
            .Select(p => new { Path = p, Name = Path.GetFileNameWithoutExtension(p) })
            .Where(x => x.Name.Contains('_'))
            .GroupBy(x => x.Name[..x.Name.IndexOf('_')]);
        foreach (var g in groups)
        {
            var ordered = g.OrderByDescending(x => x.Name).ToList();
            var newest = ordered[0];
            var ticksStr = newest.Name[(newest.Name.IndexOf('_') + 1)..];
            var fetched = long.TryParse(ticksStr, out var t) ? new DateTime(t, DateTimeKind.Utc) : File.GetLastWriteTimeUtc(newest.Path);
            _entries[g.Key] = new CacheEntry(newest.Path, fetched, new FileInfo(newest.Path).Length);
            foreach (var older in ordered.Skip(1)) TryDelete(older.Path);
        }
    }

    private static Dictionary<int, ChannelInfo> ParseLiveIndex(string path)
    {
        var map = new Dictionary<int, ChannelInfo>();
        using var fs = OpenShared(path);
        using var doc = JsonDocument.Parse(fs);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = ReadInt(el, "stream_id");
            if (id is null) continue;
            map[id.Value] = new ChannelInfo(id.Value,
                ReadString(el, "name") ?? $"Channel {id}",
                ReadString(el, "stream_icon"),
                ReadString(el, "epg_channel_id"));
        }
        return map;
    }

    internal static int? ReadInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return null;
    }

    private static string? ReadString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Describe(string path, IEnumerable<KeyValuePair<string, string>> extra) =>
        path + (extra.Any() ? "?" + string.Join("&", extra.Select(k => k.Key + "=" + k.Value)) : "");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still open somewhere; cleaned on next start */ }
    }
}
