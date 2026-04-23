using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using StuiPodcast.Infra.Feeds;
using Xunit;

namespace StuiPodcast.Infra.Tests.Feeds;

public sealed class Id3ChaptersParserTests
{
    [Fact]
    public void Returns_null_for_non_id3_stream()
    {
        var data = new byte[] { 0xFF, 0xFB, 0x00, 0x00 }; // raw MP3 frame, no ID3 tag
        using var ms = new MemoryStream(data);
        Id3ChaptersParser.TryParse(ms).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_stream_too_short()
    {
        using var ms = new MemoryStream(new byte[] { 0x49, 0x44 }); // "ID" only
        Id3ChaptersParser.TryParse(ms).Should().BeNull();
    }

    [Fact]
    public void Parses_single_chapter_v2_4()
    {
        var blob = BuildId3(version: 4, chapters: new[]
        {
            ("ch0", 0u, 30_000u, "Intro")
        });
        using var ms = new MemoryStream(blob);
        var result = Id3ChaptersParser.TryParse(ms);

        result.Should().NotBeNull();
        result!.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { StartSeconds = 0.0, Title = "Intro" });
    }

    [Fact]
    public void Parses_multiple_chapters_sorted()
    {
        var blob = BuildId3(version: 4, chapters: new[]
        {
            ("c2", 120_000u, 600_000u, "Main"),
            ("c0", 0u,       60_000u,  "Intro"),
            ("c3", 600_000u, 900_000u, "Outro"),
        });
        using var ms = new MemoryStream(blob);
        var result = Id3ChaptersParser.TryParse(ms);

        result!.Select(c => c.Title).Should().Equal("Intro", "Main", "Outro");
        result[1].StartSeconds.Should().Be(120.0);
    }

    [Fact]
    public void Parses_v2_3_with_big_endian_size()
    {
        var blob = BuildId3(version: 3, chapters: new[]
        {
            ("a", 5_000u, 10_000u, "Chapter A")
        });
        using var ms = new MemoryStream(blob);
        var result = Id3ChaptersParser.TryParse(ms);

        result!.Single().StartSeconds.Should().Be(5.0);
        result.Single().Title.Should().Be("Chapter A");
    }

    [Fact]
    public void Returns_null_for_v2_2()
    {
        // v2.2 has 3-byte frame ids — CHAP didn't exist yet.
        var blob = new byte[] { 0x49, 0x44, 0x33, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A };
        using var ms = new MemoryStream(blob);
        Id3ChaptersParser.TryParse(ms).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_no_chap_frames_present()
    {
        // Valid ID3 header but only a TIT2 artist frame — no chapters.
        var tag = new List<byte>();
        AppendFrame(tag, "TIT2", version: 4, payload: EncodeTextFrame("Song Title"));
        var blob = WrapId3(version: 4, body: tag.ToArray());
        using var ms = new MemoryStream(blob);

        Id3ChaptersParser.TryParse(ms).Should().BeNull();
    }

    // ── helpers to build synthetic ID3 tags ──────────────────────────────────

    static byte[] BuildId3(int version, (string elemId, uint startMs, uint endMs, string title)[] chapters)
    {
        var body = new List<byte>();
        foreach (var (elem, start, end, title) in chapters)
            AppendFrame(body, "CHAP", version, EncodeChap(elem, start, end, title, version));
        return WrapId3(version, body.ToArray());
    }

    static byte[] EncodeChap(string elemId, uint startMs, uint endMs, string title, int version)
    {
        var ms = new MemoryStream();
        // Element ID, null-terminated
        ms.Write(Encoding.ASCII.GetBytes(elemId));
        ms.WriteByte(0);
        // timing fields
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, startMs); ms.Write(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf, endMs);   ms.Write(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf, 0xFFFFFFFF); ms.Write(buf); // start offset unknown
        BinaryPrimitives.WriteUInt32BigEndian(buf, 0xFFFFFFFF); ms.Write(buf); // end offset unknown

        // TIT2 sub-frame
        var sub = new List<byte>();
        AppendFrame(sub, "TIT2", version, EncodeTextFrame(title));
        ms.Write(sub.ToArray());

        return ms.ToArray();
    }

    static byte[] EncodeTextFrame(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var result = new byte[utf8.Length + 1];
        result[0] = 3; // UTF-8
        Array.Copy(utf8, 0, result, 1, utf8.Length);
        return result;
    }

    static void AppendFrame(List<byte> tag, string frameId, int version, byte[] payload)
    {
        tag.AddRange(Encoding.ASCII.GetBytes(frameId));
        var sizeBytes = new byte[4];
        if (version == 4)
            WriteSynchsafe(sizeBytes, (uint)payload.Length);
        else
            BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, (uint)payload.Length);
        tag.AddRange(sizeBytes);
        tag.AddRange(new byte[] { 0, 0 }); // flags
        tag.AddRange(payload);
    }

    static byte[] WrapId3(int version, byte[] body)
    {
        var header = new byte[10];
        header[0] = (byte)'I'; header[1] = (byte)'D'; header[2] = (byte)'3';
        header[3] = (byte)version; header[4] = 0; header[5] = 0; // flags
        WriteSynchsafe(header.AsSpan(6, 4), (uint)body.Length);

        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return result;
    }

    static void WriteSynchsafe(Span<byte> dest, uint value)
    {
        dest[0] = (byte)((value >> 21) & 0x7F);
        dest[1] = (byte)((value >> 14) & 0x7F);
        dest[2] = (byte)((value >> 7)  & 0x7F);
        dest[3] = (byte)(value         & 0x7F);
    }
    static void WriteSynchsafe(byte[] dest, uint value) => WriteSynchsafe(dest.AsSpan(), value);
}
