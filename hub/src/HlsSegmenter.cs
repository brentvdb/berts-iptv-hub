namespace BertsIptvHub;

/// <summary>
/// Turns the live MPEG-TS relay into a short live HLS playlist for Chromecast / Google Cast devices.
/// No decoding or encoding: TS packets are only grouped into ~4 s segments that start at a keyframe,
/// each prefixed with the PAT/PMT tables so it plays on its own.
/// </summary>
public sealed class HlsSegmenter
{
    public sealed record Segment(long Sequence, double Duration, byte[] Data);

    private const int P = 188;
    private const double TargetSeconds = 4.0;
    private const int KeepSegments = 6;
    private const long PtsWrap = 1L << 33;

    private readonly object _lock = new();
    private readonly LinkedList<Segment> _segments = new();
    private readonly byte[] _leftover = new byte[P];
    private int _leftoverLen;

    private byte[]? _pat, _pmt;
    private int _pmtPid = -1, _videoPid = -1;

    private MemoryStream? _current;
    private long _currentStartPts = -1;
    private DateTime _currentStartWall;
    private long _nextSequence;
    private bool _keyframesSeen;
    private DateTime? _firstVideoWall;

    /// <summary>When a Cast device last asked for the playlist or a segment (keeps the stream alive).</summary>
    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;
    public bool Active => DateTime.UtcNow - LastAccessUtc < TimeSpan.FromSeconds(25);
    public void Touch() => LastAccessUtc = DateTime.UtcNow;

    public int Count { get { lock (_lock) return _segments.Count; } }

    public Segment? Get(long sequence)
    {
        lock (_lock) return _segments.FirstOrDefault(s => s.Sequence == sequence);
    }

    public string Playlist()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            var target = _segments.Count == 0 ? (int)TargetSeconds : (int)Math.Ceiling(_segments.Max(s => s.Duration));
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append(Math.Max(1, target)).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(_segments.First?.Value.Sequence ?? 0).Append('\n');
            foreach (var s in _segments)
            {
                sb.Append("#EXTINF:").Append(s.Duration.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append(s.Sequence).Append(".ts\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>The stream restarted (provider reconnect): drop the half-built segment and resync.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _leftoverLen = 0;
            _current = null;
            _currentStartPts = -1;
        }
    }

    public void Feed(byte[] chunk)
    {
        lock (_lock)
        {
            int pos = 0;
            // Complete a packet split across chunks.
            if (_leftoverLen > 0)
            {
                int need = P - _leftoverLen;
                if (chunk.Length < need)
                {
                    Buffer.BlockCopy(chunk, 0, _leftover, _leftoverLen, chunk.Length);
                    _leftoverLen += chunk.Length;
                    return;
                }
                Buffer.BlockCopy(chunk, 0, _leftover, _leftoverLen, need);
                pos = need;
                _leftoverLen = 0;
                if (_leftover[0] == 0x47) Packet(_leftover, 0);
            }

            while (pos + P <= chunk.Length)
            {
                if (chunk[pos] != 0x47)
                {
                    // Lost sync: find the next packet start.
                    int next = TsJoin.FindAlignment(chunk, pos);
                    if (next < 0) { pos = chunk.Length; break; }
                    pos = next;
                    continue;
                }
                Packet(chunk, pos);
                pos += P;
            }

            int rest = chunk.Length - pos;
            if (rest > 0 && chunk[pos] == 0x47)
            {
                Buffer.BlockCopy(chunk, pos, _leftover, 0, rest);
                _leftoverLen = rest;
            }
        }
    }

    private void Packet(byte[] d, int i)
    {
        int pid = ((d[i + 1] & 0x1F) << 8) | d[i + 2];
        bool pusi = (d[i + 1] & 0x40) != 0;
        int afc = (d[i + 3] >> 4) & 0x3;

        if (pid == 0 && pusi)
        {
            var p = TsJoin.ParsePat(d, i, afc);
            if (p > 0) { _pmtPid = p; _pat = d.AsSpan(i, P).ToArray(); }
        }
        else if (pid == _pmtPid && pusi)
        {
            var v = TsJoin.ParsePmt(d, i, afc);
            if (v > 0) { _videoPid = v; _pmt = d.AsSpan(i, P).ToArray(); }
        }

        bool cut = false;
        long pts = -1;
        if (pid == _videoPid && pusi)
        {
            _firstVideoWall ??= DateTime.UtcNow;
            bool keyframe = (afc & 0x2) != 0 && d[i + 4] > 0 && (d[i + 5] & 0x40) != 0;
            if (keyframe) _keyframesSeen = true;
            // Streams that never flag keyframes: fall back to cutting on any new frame after a while.
            bool fallback = !_keyframesSeen && DateTime.UtcNow - _firstVideoWall.Value > TimeSpan.FromSeconds(10);
            cut = keyframe || fallback;
            pts = ReadPts(d, i, afc);
        }

        if (cut && _pat != null && _pmt != null)
        {
            if (_current == null)
            {
                StartSegment(pts);
            }
            else if (SecondsSince(pts) >= TargetSeconds)
            {
                FinishSegment(SecondsSince(pts));
                StartSegment(pts);
            }
        }

        _current?.Write(d, i, P);
    }

    private void StartSegment(long pts)
    {
        _current = new MemoryStream(1024 * 1024);
        _current.Write(_pat!, 0, P);
        _current.Write(_pmt!, 0, P);
        _currentStartPts = pts;
        _currentStartWall = DateTime.UtcNow;
    }

    private void FinishSegment(double seconds)
    {
        var seg = new Segment(_nextSequence++, Math.Clamp(seconds, 0.5, 30), _current!.ToArray());
        _segments.AddLast(seg);
        while (_segments.Count > KeepSegments) _segments.RemoveFirst();
    }

    /// <summary>Segment length from video timestamps (falls back to arrival time if they're missing or odd).</summary>
    private double SecondsSince(long pts)
    {
        if (pts >= 0 && _currentStartPts >= 0)
        {
            var diff = (pts - _currentStartPts + PtsWrap) % PtsWrap;
            var s = diff / 90000.0;
            if (s > 0 && s < 30) return s;
        }
        return (DateTime.UtcNow - _currentStartWall).TotalSeconds;
    }

    private static long ReadPts(byte[] d, int i, int afc)
    {
        if ((afc & 0x1) == 0) return -1;
        int off = i + 4;
        if ((afc & 0x2) != 0) off += 1 + d[i + 4];
        if (off + 14 > i + P) return -1;
        if (d[off] != 0 || d[off + 1] != 0 || d[off + 2] != 1) return -1; // PES start code
        if ((d[off + 7] & 0x80) == 0) return -1;                           // no PTS
        int b = off + 9;
        return ((long)(d[b] >> 1) & 0x07) << 30
             | (long)d[b + 1] << 22
             | (long)(d[b + 2] >> 1) << 15
             | (long)d[b + 3] << 7
             | (long)(d[b + 4] >> 1);
    }
}
