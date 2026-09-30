using FluentAssertions;
using Podliner.Infra.Player;
using Podliner.Infra.Tests.Fakes;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// The built-in engine (#3), end to end: real decoding, real timing, output
// into miniaudio's null device, episodes served by a fake HTTP server that
// counts what was read. The mp3s are built here: Layer III frames whose side
// info and main data are all zero decode to silence, so an hour at 8 kbit/s
// is 3.6 MB and needs no fixture file.
[Collection(nameof(BuiltinAudioPlayerTests))]
public sealed class BuiltinAudioPlayerTests
{
    // MPEG-2.5 Layer III, 8 kbit/s, 8 kHz, mono: 72 bytes and 72 ms a frame
    static readonly byte[] Header = { 0xFF, 0xE3, 0x18, 0xC0 };
    const int FrameLen = 72;
    const double FrameSeconds = 576 / 8000.0;

    static byte[] Mp3(TimeSpan length)
    {
        int frames = (int)(length.TotalSeconds / FrameSeconds);
        var b = new byte[frames * FrameLen];
        for (int i = 0; i < frames; i++) Array.Copy(Header, 0, b, i * FrameLen, 4);
        return b;
    }

    static readonly byte[] Hour = Mp3(TimeSpan.FromHours(1));

    static (BuiltinAudioPlayer player, RangeFileServer server) Start(byte[] file, long? startMs = null)
    {
        var server = new RangeFileServer(file);
        var player = new BuiltinAudioPlayer(silent: true, new HttpClient(server));
        player.SetVolume(0);
        player.Play("http://x.test/ep.mp3", startMs);
        return (player, server);
    }

    static void Wait(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void It_plays_and_the_position_runs_in_real_time()
    {
        var (p, _) = Start(Hour);
        using var _p = p;

        Wait(2);

        p.State.IsPlaying.Should().BeTrue();
        p.State.Position.TotalSeconds.Should().BeInRange(1.2, 3.0);
        p.State.Length!.Value.TotalMinutes.Should().BeApproximately(60, 0.5);
    }

    [Fact]
    public void Starting_at_40_minutes_does_not_fetch_the_40_minutes_before()
    {
        var (p, server) = Start(Hour, startMs: (long)TimeSpan.FromMinutes(40).TotalMilliseconds);
        using var _p = p;

        Wait(1.5);

        p.State.Position.TotalMinutes.Should().BeApproximately(40, 0.1);
        // 40 minutes are 2.4 MB of this file; a start reads a few hundred KB
        server.BytesRead.Should().BeLessThan(1_000_000);
    }

    [Fact]
    public void A_jump_back_and_a_long_jump_ahead_land_where_asked()
    {
        var (p, server) = Start(Hour);
        using var _p = p;
        Wait(1);

        p.SeekTo(TimeSpan.FromMinutes(50));
        Wait(1);
        p.State.Position.TotalMinutes.Should().BeApproximately(50, 0.1);

        p.SeekTo(TimeSpan.FromMinutes(5));
        Wait(1);
        p.State.Position.TotalMinutes.Should().BeApproximately(5, 0.1);

        server.BytesRead.Should().BeLessThan(2_000_000);
    }

    [Fact]
    public void A_short_skip_ahead_moves_ten_seconds()
    {
        var (p, _) = Start(Hour);
        using var _p = p;
        Wait(1);

        var before = p.State.Position;
        p.SeekRelative(TimeSpan.FromSeconds(10));
        Wait(0.6);

        (p.State.Position - before).TotalSeconds.Should().BeInRange(10, 12);
    }

    [Fact]
    public void Pause_holds_the_position()
    {
        var (p, _) = Start(Hour);
        using var _p = p;
        Wait(1);

        p.TogglePause();
        var held = p.State.Position;
        Wait(1);

        p.State.IsPlaying.Should().BeFalse();
        p.State.Position.Should().BeCloseTo(held, TimeSpan.FromMilliseconds(300));

        p.TogglePause();
        Wait(1);
        p.State.Position.Should().BeGreaterThan(held + TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Double_speed_plays_twice_as_far()
    {
        var (p, _) = Start(Hour);
        using var _p = p;
        p.SetSpeed(2.0);
        Wait(0.5);

        var from = p.State.Position;
        Wait(2);

        (p.State.Position - from).TotalSeconds.Should().BeInRange(3.0, 5.0);
    }

    // SoundFlow 1.4.1 rebuilds its time stretcher's buffers inside
    // SetSpeed, on the caller's thread, while the audio thread may be using
    // them; the exception that follows is unhandled on the audio thread and
    // ends the process (seen on the macOS runner: "count ('-7168') must be a
    // non-negative value" in WsolaTimeStretcher.Process).
    [Fact]
    public void Changing_speed_over_and_over_while_playing_does_not_bring_it_down()
    {
        var (p, _) = Start(Hour);
        using var _p = p;
        Wait(0.5);

        var speeds = new[] { 1.0, 1.5, 2.0, 0.75, 1.25, 3.0, 0.5 };
        var until = DateTime.UtcNow.AddSeconds(4);
        for (int i = 0; DateTime.UtcNow < until; i++)
        {
            p.SetSpeed(speeds[i % speeds.Length]);
            if (i % 50 == 0) p.SeekRelative(TimeSpan.FromSeconds(i % 100 == 0 ? 5 : -5));
            Thread.Sleep(2);
        }
        p.SetSpeed(1.0);
        var at = p.State.Position;
        Wait(1);

        p.State.IsPlaying.Should().BeTrue();
        p.State.Position.Should().BeGreaterThan(at + TimeSpan.FromMilliseconds(500), "it still plays");
    }

    [Fact]
    public void After_the_audio_thread_throws_playback_goes_on_from_where_it_was()
    {
        var (p, _) = Start(Hour, startMs: 600_000);
        using var _p = p;
        Wait(1.5);
        var before = p.State.Position;

        p.FaultAudioThread(new ArgumentOutOfRangeException("count"));
        Wait(1.5);

        p.State.IsPlaying.Should().BeTrue();
        p.State.Position.Should().BeGreaterThan(before + TimeSpan.FromMilliseconds(700));
        p.State.Position.Should().BeLessThan(before + TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void The_end_stops_playback_at_the_full_length()
    {
        var (p, _) = Start(Mp3(TimeSpan.FromSeconds(2)));
        using var _p = p;

        Wait(3.5);

        p.State.IsPlaying.Should().BeFalse();
        p.State.Position.Should().Be(p.State.Length!.Value);
    }

    [Fact]
    public void A_downloaded_file_plays_and_seeks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"podliner-builtin-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, Hour);
        try
        {
            using var p = new BuiltinAudioPlayer(silent: true);
            p.SetVolume(0);
            p.Play(path);
            Wait(1);
            p.SeekTo(TimeSpan.FromMinutes(30));
            Wait(1);

            p.State.IsPlaying.Should().BeTrue();
            p.State.Position.TotalMinutes.Should().BeApproximately(30, 0.1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Something_that_is_not_an_mp3_is_refused()
    {
        var m4a = new byte[8192];
        "\0\0\0 ftypM4A "u8.ToArray().CopyTo(m4a, 0);
        var server = new RangeFileServer(m4a);
        using var p = new BuiltinAudioPlayer(silent: true, new HttpClient(server));

        var act = () => p.Play("http://x.test/ep.m4a");

        act.Should().Throw<NotSupportedException>();
    }
}
