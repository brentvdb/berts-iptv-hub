using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace BertsIptvHub;

public sealed record Programme(long Start, long End, string Title, string? Desc);

/// <summary>A channel found in a public XMLTV source (for matching / the mapping page).</summary>
public sealed record SourceChannel(string Source, string Id, string Name);

/// <summary>Where a live channel's guide comes from.</summary>
public sealed record GuideMatch(string Origin, string? Source, string? SourceChannelId, string? SourceChannelName);
// Origin: "provider" | "auto" (name/id match in a public source) | "manual" (chosen on the /epg page) | "none"

/// <summary>Manual channel → public-guide choices made on the hub's /epg page (data\epg-map.json).</summary>
public sealed class EpgMapStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _map = new();

    public EpgMapStore(HubOptions o)
    {
        _path = Path.Combine(o.DataDirFull, "epg-map.json");
        try
        {
            if (File.Exists(_path))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch { _map = new(); }
    }

    public int Version { get; private set; }

    public Dictionary<int, string> Snapshot()
    {
        lock (_lock)
            return _map.Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
    }

    /// <summary>sourceChannelId "" = no guide for this channel; null = remove the manual choice (back to automatic).</summary>
    public void Set(int streamId, string? sourceChannelId)
    {
        lock (_lock)
        {
            if (sourceChannelId is null) _map.Remove(streamId.ToString());
            else _map[streamId.ToString()] = sourceChannelId;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map, Json));
            File.Move(tmp, _path, overwrite: true);
            Version++;
        }
    }
}

/// <summary>
/// Builds the guide for every live channel on the PC (not on the TV boxes):
/// 1. the provider's own XMLTV, by epg_channel_id;
/// 2. for channels the provider has no guide for, free public XMLTV sources (e.g. epgshare01),
///    matched by channel id or by a normalised channel name ("UK: SKY SPORTS F1 FHD" ⇔ "Sky Sports F1 HD");
/// 3. manual choices from the /epg page override both.
/// Rebuilt automatically whenever any of those inputs change.
/// </summary>
public sealed class EpgIndex
{
    public sealed class Built
    {
        public static readonly Built Empty = new();
        public Dictionary<int, Programme[]> ByStream { get; init; } = new();
        public Dictionary<int, GuideMatch> Matches { get; init; } = new();
        public List<SourceChannel> SourceChannels { get; init; } = new();
        public string Signature { get; init; } = "";
        public DateTime BuiltUtc { get; init; }
        public int FromProvider { get; init; }
        public int FromPublic { get; init; }
        public int Manual { get; init; }
    }

    private static readonly TimeSpan Before = TimeSpan.FromHours(3);
    private static readonly TimeSpan After = TimeSpan.FromHours(36);
    private const int MaxDesc = 400;

    private readonly HubOptions _o;
    private readonly ProviderClient _provider;
    private readonly EpgMapStore _map;
    private readonly ILogger<EpgIndex> _log;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile Built _built = Built.Empty;

    public EpgIndex(HubOptions o, ProviderClient provider, EpgMapStore map, ILogger<EpgIndex> log)
    {
        _o = o;
        _provider = provider;
        _map = map;
        _log = log;
    }

    public Built Current => _built;

    public async Task<Built> GetAsync(CancellationToken ct)
    {
        var live = await _provider.GetLiveIndexAsync(ct);
        ProviderClient.CacheEntry? providerXml = null;
        try { providerXml = await _provider.GetXmltvAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning("Provider EPG unavailable: {Msg}", ex.Message); }

        var extras = new List<(string label, string path)>();
        foreach (var url in _o.EffectiveExtraEpgSources)
        {
            try { extras.Add((SourceLabel(url), (await _provider.GetUrlAsync(url, _provider.EpgTtl, ct)).Path)); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning("Public EPG {Url} unavailable: {Msg}", url, ex.Message); }
        }

        var signature = string.Join("|", providerXml?.Path ?? "-", _provider.LiveIndexVersion, _map.Version,
            string.Join(",", extras.Select(e => e.path)));
        if (signature == _built.Signature) return _built;

        await _buildLock.WaitAsync(ct);
        try
        {
            if (signature == _built.Signature) return _built;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = await Task.Run(() => Build(live, providerXml?.Path, extras, _map.Snapshot(), signature), ct);
            _built = built;
            _log.LogInformation("Guide built in {S:N1}s: {Provider} channels from the provider, {Public} from public guides, {Manual} chosen manually, {Total} live channels",
                sw.Elapsed.TotalSeconds, built.FromProvider, built.FromPublic, built.Manual, live.Count);
            return built;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    public static (Programme? now, Programme? next) NowNext(Programme[] list, long nowMs)
    {
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].End <= nowMs) continue;
            if (list[i].Start <= nowMs)
                return (list[i], i + 1 < list.Length ? list[i + 1] : null);
            return (null, list[i]);
        }
        return (null, null);
    }

    // ---- build ----------------------------------------------------------------------------------

    private Built Build(IReadOnlyDictionary<int, ChannelInfo> live, string? providerPath,
        List<(string label, string path)> extras, Dictionary<int, string> manual, string signature)
    {
        var byStream = new Dictionary<int, Programme[]>();
        var matches = new Dictionary<int, GuideMatch>();
        int fromProvider = 0, fromPublic = 0, manualCount = 0;

        // 1. Provider guide.
        var provider = providerPath is null
            ? new Dictionary<string, List<Programme>>(StringComparer.OrdinalIgnoreCase)
            : Parse(providerPath, "provider", null).programmes;
        foreach (var ch in live.Values)
        {
            if (manual.ContainsKey(ch.Id)) continue;
            if (!string.IsNullOrEmpty(ch.EpgChannelId) && provider.TryGetValue(ch.EpgChannelId, out var list) && list.Count > 0)
            {
                byStream[ch.Id] = Sorted(list);
                matches[ch.Id] = new GuideMatch("provider", null, ch.EpgChannelId, null);
                fromProvider++;
            }
        }

        // 2./3. Public sources for channels still without a guide (and manual choices).
        var needing = live.Values.Where(c => !byStream.ContainsKey(c.Id)).ToList();
        var allSourceChannels = new List<SourceChannel>();
        foreach (var (label, path) in extras)
        {
            var pending = new Dictionary<int, (string id, string name, bool manual)>();
            var (programmes, channels) = Parse(path, label, sourceChannels =>
            {
                var byId = new Dictionary<string, SourceChannel>(StringComparer.OrdinalIgnoreCase);
                var byName = new Dictionary<string, SourceChannel>();
                foreach (var sc in sourceChannels)
                {
                    byId.TryAdd(sc.Id, sc);
                    foreach (var key in new[] { Normalize(sc.Name), NormalizeId(sc.Id) })
                        if (key.Length >= 3) byName.TryAdd(key, sc);
                }
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ch in needing)
                {
                    if (matches.ContainsKey(ch.Id)) continue;
                    SourceChannel? hit = null;
                    bool isManual = false;
                    if (manual.TryGetValue(ch.Id, out var chosen))
                    {
                        if (chosen.Length == 0 || !byId.TryGetValue(chosen, out hit)) continue; // "no guide" or another source
                        isManual = true;
                    }
                    else if (!string.IsNullOrEmpty(ch.EpgChannelId) && byId.TryGetValue(ch.EpgChannelId, out var idHit)) hit = idHit;
                    else if (byName.TryGetValue(Normalize(ch.Name), out var nameHit)) hit = nameHit;
                    if (hit is null) continue;
                    wanted.Add(hit.Id);
                    pending[ch.Id] = (hit.Id, hit.Name, isManual);
                }
                return wanted;
            });
            allSourceChannels.AddRange(channels);

            foreach (var (streamId, (id, name, isManual)) in pending)
            {
                if (!programmes.TryGetValue(id, out var list) || list.Count == 0) continue;
                byStream[streamId] = Sorted(list);
                matches[streamId] = new GuideMatch(isManual ? "manual" : "auto", label, id, name);
                if (isManual) manualCount++; else fromPublic++;
            }
        }

        foreach (var ch in live.Values)
            matches.TryAdd(ch.Id, new GuideMatch(manual.TryGetValue(ch.Id, out var m) && m.Length == 0 ? "manual-none" : "none", null, null, null));

        return new Built
        {
            ByStream = byStream,
            Matches = matches,
            SourceChannels = allSourceChannels,
            Signature = signature,
            BuiltUtc = DateTime.UtcNow,
            FromProvider = fromProvider,
            FromPublic = fromPublic,
            Manual = manualCount,
        };
    }

    private static Programme[] Sorted(List<Programme> list) => list.OrderBy(p => p.Start).ToArray();

    /// <summary>
    /// Streams an XMLTV file (plain or gzipped). <paramref name="decideWanted"/> is called once with the
    /// file's channel list (channels come before programmes in XMLTV) and returns which channel ids to keep;
    /// null keeps everything.
    /// </summary>
    private (Dictionary<string, List<Programme>> programmes, List<SourceChannel> channels) Parse(
        string path, string label, Func<List<SourceChannel>, HashSet<string>>? decideWanted)
    {
        var from = DateTimeOffset.UtcNow - Before;
        var to = DateTimeOffset.UtcNow + After;
        var programmes = new Dictionary<string, List<Programme>>(StringComparer.OrdinalIgnoreCase);
        var channels = new List<SourceChannel>();
        HashSet<string>? wanted = null;
        bool decided = decideWanted is null;
        int count = 0;

        using var file = ProviderClient.OpenShared(path);
        Stream stream = file;
        var b1 = file.ReadByte();
        var b2 = file.ReadByte();
        file.Position = 0;
        if (b1 == 0x1f && b2 == 0x8b) stream = new GZipStream(file, CompressionMode.Decompress);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
                {
                    reader.Read();
                    continue;
                }

                if (reader.Name == "channel")
                {
                    var id = reader.GetAttribute("id");
                    string? name = null;
                    using (var sub = reader.ReadSubtree())
                    {
                        while (sub.Read())
                            if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name" && name is null)
                                name = sub.ReadElementContentAsString();
                    }
                    reader.Read();
                    if (!string.IsNullOrWhiteSpace(id)) channels.Add(new SourceChannel(label, id, string.IsNullOrWhiteSpace(name) ? id : name.Trim()));
                    continue;
                }

                if (reader.Name != "programme")
                {
                    reader.Skip();
                    continue;
                }

                if (!decided)
                {
                    wanted = decideWanted!(channels);
                    decided = true;
                }

                var channel = reader.GetAttribute("channel");
                var start = ParseTime(reader.GetAttribute("start"));
                var stop = ParseTime(reader.GetAttribute("stop"));
                if (channel is null || start is null || stop is null || stop <= from || start >= to ||
                    (wanted != null && !wanted.Contains(channel)))
                {
                    reader.Skip();
                    continue;
                }

                string? title = null, desc = null;
                using (var sub = reader.ReadSubtree())
                {
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element) continue;
                        if (title is null && sub.Name == "title") title = sub.ReadElementContentAsString();
                        else if (desc is null && sub.Name == "desc") desc = sub.ReadElementContentAsString();
                    }
                }
                reader.Read();
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (desc is { Length: > MaxDesc }) desc = desc[..MaxDesc].TrimEnd() + "…";

                if (!programmes.TryGetValue(channel, out var list)) programmes[channel] = list = new List<Programme>();
                list.Add(new Programme(start.Value.ToUnixTimeMilliseconds(), stop.Value.ToUnixTimeMilliseconds(), title.Trim(), desc?.Trim()));
                count++;
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            _log.LogWarning("{Label} guide file is not fully readable ({Msg}); using {N} programmes read before the error", label, ex.Message, count);
        }
        finally
        {
            if (!ReferenceEquals(stream, file)) stream.Dispose();
        }

        if (!decided && decideWanted != null) decideWanted(channels); // file had no programmes
        return (programmes, channels);
    }

    // ---- matching -------------------------------------------------------------------------------

    private static readonly Regex CountryPrefix = new(@"^\W*(?:[a-z]{2,4})\s*(?:[:|\]\)\-»]|\s\|)\s*", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex Noise = new(@"\b(?:fhd|uhd|hd|sd|4k|8k|hevc|h\.?26[45]|\d{2,3}\s?fps|fps|hdr|raw|vip|backup|alt|multi|ultra hd|full hd)\b", RegexOptions.Compiled);
    private static readonly Regex TrailingCountry = new(@"\b(?:uk|gb|ie|za|us|en)\s*$", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9]", RegexOptions.Compiled);

    /// <summary>"UK: SKY SPORTS F1 ᴴᴰ" → "skysportsf1"; "Sky Sports F1 HD" → "skysportsf1".</summary>
    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormKC);
        for (int i = 0; i < 2; i++) s = CountryPrefix.Replace(s, "");
        s = Brackets.Replace(s, " ");
        s = s.Replace("&", " and ").Replace("+", " plus ");
        s = Noise.Replace(s, " ");
        s = TrailingCountry.Replace(s.Trim(), "");
        return NonAlnum.Replace(s, "");
    }

    /// <summary>XMLTV ids like "SkySp.F1.HD.uk" / "SkySpMainEvHD.uk" → "skysportsf1" / "skysportsmainevent".</summary>
    public static string NormalizeId(string id)
    {
        var s = id.ToLowerInvariant();
        s = Regex.Replace(s, @"\.(?:uk|gb|ie|za|us|[a-z]{2})$", "");
        s = Regex.Replace(s, @"skysp(?!orts)", "skysports");
        s = Regex.Replace(s, @"fball", "football");
        s = Regex.Replace(s, @"mainev(?!ent)", "mainevent");
        s = Regex.Replace(s, @"boxoff(?!ice)", "boxoffice");
        s = Regex.Replace(s, @"skysports[ .]?pl\b", "skysportspremierleague");
        s = s.Replace("+", " plus ").Replace('.', ' ');
        s = Regex.Replace(s, @"\b(?:hd|uhd|fhd|sd)\b", " ");
        s = NonAlnum.Replace(s, "");
        if (s.EndsWith("hd") && s.Length > 4) s = s[..^2];
        return s;
    }

    public static string SourceLabel(string url)
    {
        var name = url.Split('?')[0].TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var ext in new[] { ".gz", ".xml", ".xz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
        if (name.StartsWith("epg_ripper_", StringComparison.OrdinalIgnoreCase)) name = name["epg_ripper_".Length..];
        return string.IsNullOrWhiteSpace(name) ? url : name;
    }

    /// <summary>XMLTV time: "20261124080000 +0200" (offset optional, then UTC).</summary>
    public static DateTimeOffset? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var parts = s.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var digits = parts[0];
        if (digits.Length < 12) return null;
        if (digits.Length > 14) digits = digits[..14];
        if (digits.Length == 12) digits += "00";
        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;

        var offset = TimeSpan.Zero;
        if (parts.Length > 1)
        {
            var o = parts[1].Trim().Replace(":", "");
            if (o.Length == 5 && (o[0] == '+' || o[0] == '-') &&
                int.TryParse(o.AsSpan(1, 2), out var hh) && int.TryParse(o.AsSpan(3, 2), out var mm))
            {
                offset = new TimeSpan(hh, mm, 0);
                if (o[0] == '-') offset = -offset;
            }
        }
        return new DateTimeOffset(dt, offset);
    }
}
