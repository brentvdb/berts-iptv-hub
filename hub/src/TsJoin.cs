namespace BertsIptvHub;

/// <summary>
/// Picks a good starting point inside the join buffer so a TV that joins an already-running
/// stream starts at a keyframe (instant picture, close to live) instead of waiting for one.
/// Only reads MPEG-TS packet headers and the PAT/PMT tables — no decoding.
/// </summary>
public static class TsJoin
{
    private const int P = 188;
    private static readonly HashSet<byte> VideoStreamTypes = new() { 0x01, 0x02, 0x10, 0x1B, 0x24, 0x27, 0x33 };

    public static byte[] BuildPrefix(byte[] data)
    {
        int start = FindAlignment(data, 0);
        if (start < 0) return Array.Empty<byte>();

        int firstPat = -1, patPos = -1, pmtPos = -1, pmtPid = -1, videoPid = -1, keyPos = -1;

        for (int i = start; i + P <= data.Length; i += P)
        {
            if (data[i] != 0x47)
            {
                int r = FindAlignment(data, i);
                if (r < 0) break;
                i = r - P;
                continue;
            }

            int pid = ((data[i + 1] & 0x1F) << 8) | data[i + 2];
            bool pusi = (data[i + 1] & 0x40) != 0;
            int afc = (data[i + 3] >> 4) & 0x3;

            if (pid == 0 && pusi)
            {
                if (firstPat < 0) firstPat = i;
                var p = ParsePat(data, i, afc);
                if (p > 0) { pmtPid = p; patPos = i; }
            }
            else if (pid == pmtPid && pusi)
            {
                var v = ParsePmt(data, i, afc);
                if (v > 0) { videoPid = v; pmtPos = i; }
            }
            else if (pid == videoPid && pusi && (afc & 0x2) != 0 && data[i + 4] > 0 && (data[i + 5] & 0x40) != 0)
            {
                keyPos = i; // random_access_indicator set on a new video frame = keyframe
            }
        }

        if (keyPos >= 0 && patPos >= 0 && pmtPos >= 0)
        {
            var tail = data.Length - keyPos;
            var result = new byte[P * 2 + tail];
            Buffer.BlockCopy(data, patPos, result, 0, P);
            Buffer.BlockCopy(data, pmtPos, result, P, P);
            Buffer.BlockCopy(data, keyPos, result, P * 2, tail);
            return result;
        }

        var from = firstPat >= 0 ? firstPat : start;
        return data[from..];
    }

    /// <summary>Finds a byte position where three consecutive TS sync bytes line up.</summary>
    public static int FindAlignment(byte[] data, int from)
    {
        for (int i = from; i + 2 * P < data.Length; i++)
            if (data[i] == 0x47 && data[i + P] == 0x47 && data[i + 2 * P] == 0x47)
                return i;
        return -1;
    }

    internal static int PayloadStart(byte[] d, int i, int afc)
    {
        if ((afc & 0x1) == 0) return -1;
        int off = i + 4;
        if ((afc & 0x2) != 0) off += 1 + d[i + 4];
        if (off >= i + P) return -1;
        off += 1 + d[off]; // pointer_field
        return off < i + P ? off : -1;
    }

    internal static int ParsePat(byte[] d, int i, int afc)
    {
        int off = PayloadStart(d, i, afc);
        if (off < 0 || off + 8 > i + P || d[off] != 0x00) return -1;
        int sectionLength = ((d[off + 1] & 0x0F) << 8) | d[off + 2];
        int end = Math.Min(off + 3 + sectionLength - 4, i + P);
        for (int j = off + 8; j + 4 <= end; j += 4)
        {
            int program = (d[j] << 8) | d[j + 1];
            int pid = ((d[j + 2] & 0x1F) << 8) | d[j + 3];
            if (program != 0) return pid;
        }
        return -1;
    }

    internal static int ParsePmt(byte[] d, int i, int afc)
    {
        int off = PayloadStart(d, i, afc);
        if (off < 0 || off + 12 > i + P || d[off] != 0x02) return -1;
        int sectionLength = ((d[off + 1] & 0x0F) << 8) | d[off + 2];
        int programInfoLength = ((d[off + 10] & 0x0F) << 8) | d[off + 11];
        int end = Math.Min(off + 3 + sectionLength - 4, i + P);
        for (int j = off + 12 + programInfoLength; j + 5 <= end;)
        {
            byte type = d[j];
            int pid = ((d[j + 1] & 0x1F) << 8) | d[j + 2];
            int esInfo = ((d[j + 3] & 0x0F) << 8) | d[j + 4];
            if (VideoStreamTypes.Contains(type)) return pid;
            j += 5 + esInfo;
        }
        return -1;
    }
}
