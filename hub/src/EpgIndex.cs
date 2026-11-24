using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace BertsIptvHub;

public sealed record Programme(long Start, long End, string Title, string? Desc);

/// <summary>
/// Parses the provider's XMLTV guide once per update (on the PC, not on every TV box) and keeps a
/// rolling window of programmes in memory, so TVs can ask for now/next or one channel's listings
/// and get a small JSON answer instantly.
/// </summary>
public sealed class EpgIndex
{
    private static readonly TimeSpan Before = TimeSpan.FromHours(3);
    private static readonly TimeSpan After = TimeSpan.FromHours(36);
    private const int MaxDesc = 400;

    private readonly ProviderClient _provider;
    private readonly ILogger<EpgIndex> _log;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile Dictionary<string, Programme[]> _byChannel = new(StringComparer.OrdinalIgnoreCase);
    private string? _builtFrom;

    public EpgIndex(ProviderClient provider, ILogger<EpgIndex> log)
    {
        _provider = provider;
        _log = log;
    }

    public DateTime? BuiltUtc { get; private set; }
    public int ChannelCount => _byChannel.Count;

    /// <summary>Makes sure the index matches the latest cached XMLTV file (rebuilds after each EPG update).</summary>
    public async Task<Dictionary<string, Programme[]>> GetAsync(CancellationToken ct)
    {
        var entry = await _provider.GetXmltvAsync(ct);
        if (entry.Path == _builtFrom) return _byChannel;

        await _buildLock.WaitAsync(ct);
        try
        {
            if (entry.Path == _builtFrom) return _byChannel;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = await Task.Run(() => Parse(entry.Path), ct);
            _byChannel = built.map;
            _builtFrom = entry.Path;
            BuiltUtc = DateTime.UtcNow;
            _log.LogInformation("EPG index built: {Channels} channels, {Programmes} programmes in {S:N1}s",
                built.map.Count, built.count, sw.Elapsed.TotalSeconds);
            return _byChannel;
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
            return (null, list[i]); // gap: nothing on right now, this is next
        }
        return (null, null);
    }

    private (Dictionary<string, Programme[]> map, int count) Parse(string path)
    {
        var from = DateTimeOffset.UtcNow - Before;
        var to = DateTimeOffset.UtcNow + After;
        var temp = new Dictionary<string, List<Programme>>(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        using var file = ProviderClient.OpenShared(path);
        Stream stream = file;
        // Some providers serve a gzipped guide without a Content-Encoding header.
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
            while (reader.ReadToFollowing("programme"))
            {
                var channel = reader.GetAttribute("channel");
                var start = ParseTime(reader.GetAttribute("start"));
                var stop = ParseTime(reader.GetAttribute("stop"));
                if (channel is null || start is null || stop is null || stop <= from || start >= to)
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
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (desc is { Length: > MaxDesc }) desc = desc[..MaxDesc].TrimEnd() + "…";

                if (!temp.TryGetValue(channel, out var list)) temp[channel] = list = new List<Programme>();
                list.Add(new Programme(start.Value.ToUnixTimeMilliseconds(), stop.Value.ToUnixTimeMilliseconds(), title.Trim(), desc?.Trim()));
                count++;
            }
        }
        catch (XmlException ex)
        {
            // Keep whatever parsed before the broken part of the file.
            _log.LogWarning("EPG file is not fully valid XML ({Msg}); using {N} programmes read before the error", ex.Message, count);
        }
        finally
        {
            if (!ReferenceEquals(stream, file)) stream.Dispose();
        }

        var map = new Dictionary<string, Programme[]>(temp.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in temp)
            map[k] = v.OrderBy(p => p.Start).ToArray();
        return (map, count);
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
