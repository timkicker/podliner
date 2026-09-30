using FluentAssertions;
using Podliner.Infra.Player;
using Podliner.Infra.Tests.Fakes;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// The built-in engine reads episodes through this: a seekable stream over
// HTTP that fetches from wherever it is asked to read, so a jump opens one
// request at the new byte instead of downloading what lies before it.
public sealed class HttpRangeStreamTests
{
    static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 % 251)).ToArray();

    static byte[] ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count) { int n = s.Read(buf, got, count - got); if (n <= 0) break; got += n; }
        return buf[..got];
    }

    [Fact]
    public void Knows_the_length_and_reads_from_the_start()
    {
        var data = Data(100_000);
        using var s = new HttpRangeStream(new HttpClient(new RangeFileServer(data)), "http://x.test/a.mp3");

        s.Length.Should().Be(100_000);
        ReadExactly(s, 1000).Should().Equal(data[..1000]);
    }

    [Fact]
    public void A_jump_opens_one_request_at_the_new_byte()
    {
        var data = Data(1_000_000);
        var server = new RangeFileServer(data);
        using var s = new HttpRangeStream(new HttpClient(server), "http://x.test/a.mp3");
        ReadExactly(s, 10);

        s.Position = 800_000;
        ReadExactly(s, 500).Should().Equal(data[800_000..800_500]);

        server.Requests.Select(r => r.From).Should().Contain(800_000);
    }

    [Fact]
    public void Reading_on_does_not_open_new_requests()
    {
        var server = new RangeFileServer(Data(500_000));
        using var s = new HttpRangeStream(new HttpClient(server), "http://x.test/a.mp3");

        ReadExactly(s, 200_000);
        var before = server.Requests.Count;
        ReadExactly(s, 200_000);

        server.Requests.Count.Should().Be(before);
    }

    [Fact]
    public void A_short_step_forward_is_read_over_not_reopened()
    {
        var data = Data(500_000);
        var server = new RangeFileServer(data);
        using var s = new HttpRangeStream(new HttpClient(server), "http://x.test/a.mp3");
        ReadExactly(s, 100);
        var before = server.Requests.Count;

        s.Seek(2000, SeekOrigin.Current);
        ReadExactly(s, 100).Should().Equal(data[2100..2200]);

        server.Requests.Count.Should().Be(before);
    }

    // miniaudio reads a little, goes back to the start and reads again,
    // four to six times while a decoder starts; each of those opened a new
    // request, a round trip each over a real network.
    [Fact]
    public void Going_back_over_what_was_just_read_needs_no_new_request()
    {
        var data = Data(500_000);
        var server = new RangeFileServer(data);
        using var s = new HttpRangeStream(new HttpClient(server), "http://x.test/a.mp3");
        s.Position = 44;
        ReadExactly(s, 20_000);
        var before = server.Requests.Count;

        for (int i = 0; i < 5; i++)
        {
            s.Position = 44;
            ReadExactly(s, 3000).Should().Equal(data[44..3044]);
        }
        s.Position = 10_000;
        ReadExactly(s, 30_000).Should().Equal(data[10_000..40_000]);

        server.Requests.Count.Should().Be(before);
    }

    // Any mix of jumps and reads gives the file's bytes: forward, back into
    // the window, back past it, far ahead, small steps.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Random_jumps_and_reads_always_give_the_files_bytes(int seed, bool ranges)
    {
        var data = Data(3_000_000);
        using var s = new HttpRangeStream(new HttpClient(new RangeFileServer(data, ranges)), "http://x.test/a.mp3");
        var rnd = new Random(seed);

        for (int i = 0; i < 300; i++)
        {
            long at = rnd.Next(4) switch
            {
                0 => rnd.Next(data.Length),
                1 => Math.Max(0, s.Position - rnd.Next(1_500_000)),
                2 => Math.Min(data.Length, s.Position + rnd.Next(300_000)),
                _ => s.Position,
            };
            s.Position = at;
            int len = rnd.Next(1, 70_000);
            var got = ReadExactly(s, len);
            got.Should().Equal(data[(int)at..(int)Math.Min(data.Length, at + len)], $"step {i} at {at} len {len}");
        }
    }

    // Feed enclosures run through 2 to 5 tracker redirects (podtrac and
    // friends, measured on NPR, Simplecast, Megaphone). Every jump would pay
    // all of them again, and count as another download each time.
    [Fact]
    public void After_the_first_request_it_asks_where_the_redirects_ended()
    {
        var server = new RangeFileServer(Data(1_000_000)) { RedirectTo = "http://cdn.test/final.mp3" };
        using var s = new HttpRangeStream(new HttpClient(server), "http://tracker.test/redirect.mp3");
        ReadExactly(s, 10);

        s.Position = 700_000;
        ReadExactly(s, 10);
        s.Position = 100;
        ReadExactly(s, 10);

        server.Urls.First().Should().Be("http://tracker.test/redirect.mp3");
        server.Urls.Skip(1).Should().OnlyContain(u => u == "http://cdn.test/final.mp3");
    }

    [Fact]
    public void An_expired_redirect_target_is_found_again_through_the_feed_url()
    {
        var data = Data(1_000_000);
        var server = new RangeFileServer(data) { RedirectTo = "http://cdn.test/final.mp3?token=1" };
        using var s = new HttpRangeStream(new HttpClient(server), "http://tracker.test/redirect.mp3");
        ReadExactly(s, 10);

        server.RedirectTargetExpired = true;
        s.Position = 600_000;

        ReadExactly(s, 100).Should().Equal(data[600_000..600_100]);
        server.Urls.Last().Should().Be("http://tracker.test/redirect.mp3");
    }

    [Fact]
    public void A_server_that_ignores_range_still_gives_the_right_bytes()
    {
        var data = Data(300_000);
        using var s = new HttpRangeStream(new HttpClient(new RangeFileServer(data, ranges: false)), "http://x.test/a.mp3");
        ReadExactly(s, 10);

        s.Position = 250_000;
        ReadExactly(s, 100).Should().Equal(data[250_000..250_100]);
    }

    [Fact]
    public void Reading_past_the_end_returns_0()
    {
        using var s = new HttpRangeStream(new HttpClient(new RangeFileServer(Data(1000))), "http://x.test/a.mp3");

        s.Position = 1000;
        s.Read(new byte[10], 0, 10).Should().Be(0);
    }

    [Fact]
    public void Sends_podliners_user_agent()
    {
        // CDNs in front of Buzzsprout and others answer 403 to engine UAs
        var server = new RangeFileServer(Data(1000));
        using var s = new HttpRangeStream(new HttpClient(server), "http://x.test/a.mp3");
        ReadExactly(s, 10);

        server.Requests.Should().OnlyContain(r => r.UserAgent!.Contains("podliner"));
    }
}
