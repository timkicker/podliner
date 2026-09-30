using FluentAssertions;
using Podliner.Infra.Player;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// The view the built-in engine's decoder reads through. miniaudio takes a
// read that returns less than it asked for as the end of the file, so this
// has to fill every read even when the stream below hands out pieces.
public sealed class SegmentStreamTests
{
    // Hands out at most 100 bytes a read, as a network stream may.
    sealed class Trickle : MemoryStream
    {
        public Trickle(byte[] b) : base(b) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 100));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 100)]);
    }

    static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public void Every_read_is_filled_until_the_end()
    {
        var data = Data(10_000);
        var s = new SegmentStream(new Trickle(data), 1000);
        var buf = new byte[4096];

        s.Read(buf, 0, 4096).Should().Be(4096);
        buf.Should().Equal(data[1000..5096]);
        s.Read(buf, 0, 4096).Should().Be(4096);
        s.Read(buf, 0, 4096).Should().Be(808);
        s.Read(buf, 0, 4096).Should().Be(0);
    }

    [Fact]
    public void While_probing_the_file_ends_early()
    {
        var s = new SegmentStream(new MemoryStream(Data(2_000_000)), 0) { Probing = true };
        var buf = new byte[64 * 1024];
        long total = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) total += n;

        total.Should().Be(256 * 1024);
        s.Probing = false;
        s.Read(buf, 0, buf.Length).Should().BeGreaterThan(0);
    }
}
