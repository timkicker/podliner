namespace Podliner.Infra.Player;

// What the built-in engine needs to know about an mp3 before it plays:
// where the audio starts, how long it runs and which byte a given time sits
// at. The decoder could tell the length only by reading the whole file, and
// could reach minute 45 only by decoding everything before it, which over
// HTTP means downloading it (measured: 43 MB for a jump to 45:00 in a
// 128 kbit/s hour). With this the engine opens the file right at the byte.
internal sealed record Mp3Info(
    long AudioStart,        // first frame, after any ID3v2 tag
    long? DataBytes,        // audio bytes from AudioStart on, if known
    int SampleRate,
    int Channels,
    int BitrateKbps,        // of the first frame; the average for CBR
    TimeSpan? Duration,
    byte[]? Toc)            // Xing table: percent of time -> 1/256 of the bytes
{
    // Byte to start reading at to land near `time`. Exact for CBR, within a
    // few seconds for VBR with a Xing table, proportional otherwise.
    public long OffsetFor(TimeSpan time)
    {
        if (Duration is not { } total || total <= TimeSpan.Zero || DataBytes is not { } bytes || bytes <= 0)
            return AudioStart;

        var frac = Math.Clamp(time / total, 0.0, 0.999);
        double at;
        if (Toc is { Length: 100 })
        {
            var p = frac * 100;
            int a = (int)p;
            double fa = Toc[a];
            double fb = a < 99 ? Toc[a + 1] : 256;
            at = (fa + (fb - fa) * (p - a)) / 256.0;
        }
        else at = frac;

        return AudioStart + (long)(at * bytes);
    }
}

internal static class Mp3Probe
{
    static readonly int[] BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    static readonly int[] BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };

    // Reads what it needs from a seekable stream and leaves the position
    // wherever it ended. Null when the start does not look like an mp3.
    public static Mp3Info? Read(Stream s)
    {
        long length;
        try { length = s.Length; } catch { length = -1; }

        s.Position = 0;
        long start = 0;
        var tag = ReadAt(s, 0, 10);
        if (tag.Length == 10 && tag[0] == 'I' && tag[1] == 'D' && tag[2] == '3')
        {
            long size = (tag[6] & 0x7F) << 21 | (tag[7] & 0x7F) << 14 | (tag[8] & 0x7F) << 7 | (tag[9] & 0x7F);
            start = 10 + size + ((tag[5] & 0x10) != 0 ? 10 : 0);
        }

        // Find the first header that is followed by another one where it
        // says it ends; a lone 0xFF 0xFx in junk is not a frame.
        var buf = ReadAt(s, start, 64 * 1024);
        for (int i = 0; i + 4 <= buf.Length; i++)
        {
            if (!TryHeader(buf, i, out var h)) continue;
            int next = i + h.FrameLen;
            if (next + 4 <= buf.Length && !(TryHeader(buf, next, out var h2) && h2.SampleRate == h.SampleRate))
                continue;
            return Build(buf, i, start + i, h, length);
        }
        return null;
    }

    static Mp3Info Build(byte[] buf, int at, long audioStart, Header h, long length)
    {
        long? dataBytes = length > audioStart ? length - audioStart : null;
        TimeSpan? duration = null;
        byte[]? toc = null;

        // Xing/Info right after the side info of the first frame
        int xo = at + 4 + (h.Mpeg1 ? (h.Channels == 1 ? 17 : 32) : (h.Channels == 1 ? 9 : 17));
        if (xo + 8 <= buf.Length && (Tag(buf, xo, "Xing") || Tag(buf, xo, "Info")))
        {
            int flags = Be32(buf, xo + 4);
            int o = xo + 8;
            if ((flags & 1) != 0 && o + 4 <= buf.Length)
            {
                long frames = (uint)Be32(buf, o); o += 4;
                if (frames > 0) duration = TimeSpan.FromSeconds(frames * (double)h.SamplesPerFrame / h.SampleRate);
            }
            if ((flags & 2) != 0 && o + 4 <= buf.Length)
            {
                long bytes = (uint)Be32(buf, o); o += 4;
                if (bytes > 0) dataBytes = bytes;
            }
            if ((flags & 4) != 0 && o + 100 <= buf.Length)
                toc = buf[o..(o + 100)];
        }
        // VBRI (Fraunhofer) sits 32 bytes after the header
        else if (at + 4 + 32 + 18 <= buf.Length && Tag(buf, at + 36, "VBRI"))
        {
            int o = at + 36 + 10;
            long bytes = (uint)Be32(buf, o);
            long frames = (uint)Be32(buf, o + 4);
            if (bytes > 0) dataBytes = bytes;
            if (frames > 0) duration = TimeSpan.FromSeconds(frames * (double)h.SamplesPerFrame / h.SampleRate);
        }

        // no header: constant bitrate, the size says how long
        if (duration == null && dataBytes is { } db && h.BitrateKbps > 0)
            duration = TimeSpan.FromSeconds(db * 8.0 / (h.BitrateKbps * 1000.0));

        return new Mp3Info(audioStart, dataBytes, h.SampleRate, h.Channels, h.BitrateKbps, duration, toc);
    }

    readonly record struct Header(bool Mpeg1, int BitrateKbps, int SampleRate, int Channels, int SamplesPerFrame, int FrameLen);

    static bool TryHeader(byte[] b, int i, out Header h)
    {
        h = default;
        if (i + 4 > b.Length || b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) return false;

        int version = (b[i + 1] >> 3) & 3;      // 0 = 2.5, 2 = 2, 3 = 1
        int layer = (b[i + 1] >> 1) & 3;        // 1 = layer III
        int brIdx = (b[i + 2] >> 4) & 0xF;
        int srIdx = (b[i + 2] >> 2) & 3;
        int pad = (b[i + 2] >> 1) & 1;
        int mode = (b[i + 3] >> 6) & 3;
        if (version == 1 || layer != 1 || brIdx is 0 or 15 || srIdx == 3) return false;

        bool v1 = version == 3;
        int br = (v1 ? BitratesV1 : BitratesV2)[brIdx];
        int sr = (v1 ? new[] { 44100, 48000, 32000 } : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 })[srIdx];
        int spf = v1 ? 1152 : 576;
        int len = (v1 ? 144 : 72) * br * 1000 / sr + pad;
        h = new Header(v1, br, sr, mode == 3 ? 1 : 2, spf, len);
        return len > 4;
    }

    static bool Tag(byte[] b, int o, string t)
    {
        if (o + t.Length > b.Length) return false;
        for (int k = 0; k < t.Length; k++) if (b[o + k] != t[k]) return false;
        return true;
    }

    static int Be32(byte[] b, int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];

    static byte[] ReadAt(Stream s, long pos, int count)
    {
        s.Position = pos;
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got == count ? buf : buf[..got];
    }
}
