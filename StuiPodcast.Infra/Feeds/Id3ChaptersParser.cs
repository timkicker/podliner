using System.Buffers.Binary;
using System.Text;
using Serilog;
using StuiPodcast.Core;

namespace StuiPodcast.Infra.Feeds;

// Minimal ID3v2 CHAP frame extractor. Most podcasts that "have chapters"
// (Spotify/PocketCasts show them) use ID3v2 CHAP frames embedded at the
// start of the MP3, not the podcast:chapters RSS extension. This parser
// handles v2.3 and v2.4 which together cover everything you'll find in
// the wild. Unsupported versions return null.
//
// Format refs: id3.org/id3v2.4.0-frames §4.10 (CHAP), §4.2 (text frames),
// id3.org/id3v2.4.0-structure §3.1 (header), §6.2 (synchsafe int).
public static class Id3ChaptersParser
{
    const int ID3_HEADER_SIZE = 10;

    // Tries to parse chapters from the beginning of an MP3 stream. Reads
    // only the tag region (announced by the header), no audio frames. If
    // the stream isn't an ID3v2 tagged MP3, returns null.
    public static List<Chapter>? TryParse(Stream stream)
    {
        try
        {
            var header = new byte[ID3_HEADER_SIZE];
            if (stream.Read(header, 0, ID3_HEADER_SIZE) != ID3_HEADER_SIZE) return null;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

            int major = header[3];
            // Only support v2.3 and v2.4. v2.2 has 3-byte frame ids and no CHAP.
            if (major != 3 && major != 4) return null;

            int tagSize = ReadSynchsafe(header, 6);
            if (tagSize <= 0 || tagSize > 64 * 1024 * 1024) return null; // sanity: 64MB cap

            var buf = new byte[tagSize];
            int read = 0;
            while (read < tagSize)
            {
                int n = stream.Read(buf, read, tagSize - read);
                if (n <= 0) return null;
                read += n;
            }

            var chapters = new List<Chapter>();
            int offset = 0;
            while (offset + 10 <= tagSize)
            {
                // Frame header: 4 bytes id, 4 bytes size, 2 bytes flags.
                if (buf[offset] == 0) break; // padding starts

                var frameId = Encoding.ASCII.GetString(buf, offset, 4);
                int frameSize = major == 4
                    ? ReadSynchsafe(buf, offset + 4)
                    : (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset + 4, 4));
                offset += 10;

                if (frameSize <= 0 || offset + frameSize > tagSize) break;

                if (frameId == "CHAP")
                {
                    var ch = ParseChap(buf, offset, frameSize, major);
                    if (ch != null) chapters.Add(ch);
                }

                offset += frameSize;
            }

            // CHAP frames are declared in CTOC order, but the tag spec
            // doesn't mandate ordering — and we've seen out-of-order in
            // the wild.
            chapters.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return chapters.Count > 0 ? chapters : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "id3/chapters parse failed");
            return null;
        }
    }

    // CHAP frame body:
    //   Element ID     <null-terminated ASCII>
    //   Start time     4 bytes big-endian ms
    //   End time       4 bytes big-endian ms
    //   Start offset   4 bytes (0xFFFFFFFF = unknown)
    //   End offset     4 bytes
    //   Sub-frames     (same layout as outer frames; title is in TIT2)
    static Chapter? ParseChap(byte[] buf, int start, int size, int major)
    {
        int end = start + size;
        int p = start;

        // Element ID — skip; we don't expose it. Null-terminated ASCII.
        while (p < end && buf[p] != 0) p++;
        if (p >= end) return null;
        p++; // skip null

        if (p + 16 > end) return null; // need 4 timing fields
        uint startMs = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p, 4)); p += 4;
        // Skip End time (p+0..4) and Start/End offsets (p+4..12).
        p += 12;

        string? title = null;
        while (p + 10 <= end)
        {
            if (buf[p] == 0) break;

            var subId = Encoding.ASCII.GetString(buf, p, 4);
            int subSize = major == 4
                ? ReadSynchsafe(buf, p + 4)
                : (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4, 4));
            p += 10;
            if (subSize <= 0 || p + subSize > end) break;

            if (subId == "TIT2" && title is null)
                title = ReadTextFrame(buf, p, subSize);

            p += subSize;
        }

        return new Chapter
        {
            StartSeconds = startMs / 1000.0,
            Title = title ?? ""
        };
    }

    // Text frame body: 1 byte encoding + bytes. Encoding:
    //   0 = ISO-8859-1, 1 = UTF-16 with BOM, 2 = UTF-16BE (2.4 only),
    //   3 = UTF-8 (2.4 only).
    static string? ReadTextFrame(byte[] buf, int start, int size)
    {
        if (size < 2) return null;
        byte enc = buf[start];
        int dataOff = start + 1;
        int dataLen = size - 1;

        // Strip a single trailing null, which is common — we always do it
        // because we only read one TIT2 value, not a list.
        Encoding encoding = enc switch
        {
            0 => Encoding.Latin1,
            1 => DetectBomUtf16(buf, ref dataOff, ref dataLen),
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => Encoding.Latin1
        };

        if (dataLen < 0) return null;
        var s = encoding.GetString(buf, dataOff, dataLen);
        return s.TrimEnd('\0');
    }

    // UTF-16 with optional BOM (FF FE = little endian, FE FF = big endian).
    // When absent the spec says "leave it to the decoder", we default LE.
    static Encoding DetectBomUtf16(byte[] buf, ref int dataOff, ref int dataLen)
    {
        if (dataLen >= 2 && buf[dataOff] == 0xFF && buf[dataOff + 1] == 0xFE)
        {
            dataOff += 2; dataLen -= 2;
            return Encoding.Unicode; // little-endian
        }
        if (dataLen >= 2 && buf[dataOff] == 0xFE && buf[dataOff + 1] == 0xFF)
        {
            dataOff += 2; dataLen -= 2;
            return Encoding.BigEndianUnicode;
        }
        return Encoding.Unicode;
    }

    // Synchsafe integer: 4 bytes, top bit of each is always 0 (so the
    // value can never contain a "sync" pattern). 7 bits × 4 = 28-bit range.
    static int ReadSynchsafe(byte[] buf, int off)
    {
        return ((buf[off] & 0x7F) << 21)
             | ((buf[off + 1] & 0x7F) << 14)
             | ((buf[off + 2] & 0x7F) << 7)
             | (buf[off + 3] & 0x7F);
    }
}
