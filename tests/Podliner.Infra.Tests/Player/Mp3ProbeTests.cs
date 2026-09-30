using FluentAssertions;
using Podliner.Infra.Player;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// The built-in engine (#3) needs two things from an mp3 before it plays a
// byte: how long it is, and where in the file a given minute starts, so a
// jump to 45:00 fetches from there instead of decoding everything before
// it. These build the headers byte by byte, no ffmpeg needed.
public sealed class Mp3ProbeTests
{
    // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo, no padding: 417 bytes a frame
    static readonly byte[] Cbr128Header = { 0xFF, 0xFB, 0x90, 0x00 };
    const int Cbr128FrameLen = 417;

    static byte[] Frames(int count, byte[] header, int frameLen)
    {
        var b = new byte[count * frameLen];
        for (int i = 0; i < count; i++) Array.Copy(header, 0, b, i * frameLen, 4);
        return b;
    }

    static byte[] Id3(int bodySize)
    {
        var b = new byte[10 + bodySize];
        b[0] = (byte)'I'; b[1] = (byte)'D'; b[2] = (byte)'3'; b[3] = 4;
        b[6] = (byte)((bodySize >> 21) & 0x7F); b[7] = (byte)((bodySize >> 14) & 0x7F);
        b[8] = (byte)((bodySize >> 7) & 0x7F);  b[9] = (byte)(bodySize & 0x7F);
        return b;
    }

    // A Xing frame (first frame, carries no audio) for MPEG-1 stereo: the tag
    // sits 4 + 32 bytes in. toc maps percent of time to 1/256ths of the bytes.
    static byte[] XingFrame(string tag, int frames, int bytes, byte[]? toc)
    {
        var f = new byte[Cbr128FrameLen];
        Array.Copy(Cbr128Header, f, 4);
        int o = 36;
        foreach (var c in tag) f[o++] = (byte)c;
        int flags = 1 | 2 | (toc != null ? 4 : 0);
        f[o++] = 0; f[o++] = 0; f[o++] = 0; f[o++] = (byte)flags;
        void Be(int v) { f[o++] = (byte)(v >> 24); f[o++] = (byte)(v >> 16); f[o++] = (byte)(v >> 8); f[o++] = (byte)v; }
        Be(frames); Be(bytes);
        if (toc != null) { Array.Copy(toc, 0, f, o, 100); o += 100; }
        return f;
    }

    static Mp3Info Probe(params byte[][] parts)
        => Mp3Probe.Read(new MemoryStream(parts.SelectMany(p => p).ToArray()))!;

    [Fact]
    public void Cbr_duration_comes_from_the_size_and_the_bitrate()
    {
        // 1000 frames of 1152 samples at 44.1 kHz: 26.12 s
        var info = Probe(Frames(1000, Cbr128Header, Cbr128FrameLen));

        info.SampleRate.Should().Be(44100);
        info.Channels.Should().Be(2);
        info.BitrateKbps.Should().Be(128);
        info.AudioStart.Should().Be(0);
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(1000 * 1152 / 44100.0, 0.1);
    }

    [Fact]
    public void An_id3_tag_is_skipped_however_large()
    {
        // album art makes tags of a megabyte and more
        var info = Probe(Id3(1_500_000), Frames(200, Cbr128Header, Cbr128FrameLen));

        info.AudioStart.Should().Be(10 + 1_500_000);
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(200 * 1152 / 44100.0, 0.1);
    }

    [Fact]
    public void A_xing_header_gives_the_exact_frame_count()
    {
        // the stream holds 10 frames, the header says 5000: the header wins
        var info = Probe(XingFrame("Xing", 5000, 2_000_000, null), Frames(10, Cbr128Header, Cbr128FrameLen));

        info.Duration!.Value.TotalSeconds.Should().BeApproximately(5000 * 1152 / 44100.0, 0.01);
    }

    [Fact]
    public void Cbr_offset_is_proportional_to_time()
    {
        var info = Probe(Id3(990), Frames(10_000, Cbr128Header, Cbr128FrameLen));

        var half = info.Duration!.Value / 2;
        var off = info.OffsetFor(half);

        off.Should().BeCloseTo(1000 + 10_000L * Cbr128FrameLen / 2, 1000);
    }

    [Fact]
    public void A_xing_toc_places_the_offset()
    {
        // first half of the time takes only a quarter of the bytes
        var toc = new byte[100];
        for (int i = 0; i < 100; i++) toc[i] = (byte)(i < 50 ? i * 64 / 50 : 64 + (i - 50) * 192 / 50);
        var xing = XingFrame("Xing", 10_000, 4_000_000, toc);
        var info = Probe(xing, Frames(10, Cbr128Header, Cbr128FrameLen));

        var off = info.OffsetFor(info.Duration!.Value / 2);

        off.Should().BeCloseTo(4_000_000L * 64 / 256, 20_000);
    }

    [Fact]
    public void Offsets_stay_inside_the_audio()
    {
        var info = Probe(Id3(90), Frames(100, Cbr128Header, Cbr128FrameLen));

        info.OffsetFor(TimeSpan.Zero).Should().Be(100);
        info.OffsetFor(TimeSpan.FromHours(5)).Should().BeLessThan(100 + 100L * Cbr128FrameLen);
        info.OffsetFor(TimeSpan.FromSeconds(-3)).Should().Be(100);
    }

    [Fact]
    public void A_false_sync_in_the_garbage_is_not_taken_for_a_frame()
    {
        // 0xFF 0xFB in junk, but no second frame where the first says it ends
        var junk = new byte[] { 0x00, 0xFF, 0xFB, 0x90, 0x00, 0x12, 0x34 };
        var info = Probe(junk, Frames(50, Cbr128Header, Cbr128FrameLen));

        info.AudioStart.Should().Be(junk.Length);
    }

    [Fact]
    public void Mpeg2_mono_at_22khz()
    {
        // MPEG-2 Layer III, 64 kbit/s, 22.05 kHz, mono: 576 samples, 72*64000/22050 = 208 bytes
        var h = new byte[] { 0xFF, 0xF3, 0x80, 0xC0 };
        var info = Probe(Frames(500, h, 208));

        info.SampleRate.Should().Be(22050);
        info.Channels.Should().Be(1);
        info.BitrateKbps.Should().Be(64);
        info.Duration!.Value.TotalSeconds.Should().BeApproximately(500 * 576 / 22050.0, 0.1);
    }

    [Fact]
    public void Not_an_mp3_gives_null()
    {
        var m4a = new byte[4096];
        "\0\0\0 ftypM4A "u8.ToArray().CopyTo(m4a, 0);

        Mp3Probe.Read(new MemoryStream(m4a)).Should().BeNull();
    }
}
