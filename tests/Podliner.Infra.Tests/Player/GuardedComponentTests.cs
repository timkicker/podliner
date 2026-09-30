using FluentAssertions;
using Podliner.Infra.Player;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Xunit;

namespace Podliner.Infra.Tests.Player;

// An exception on the audio thread is unhandled and ends the process; the
// macOS runner saw one from inside SoundFlow's time stretcher. The built-in
// engine plays through this guard, which turns any such exception into
// silence and a report instead.
public sealed class GuardedComponentTests
{
    static readonly AudioFormat Fmt = new()
    {
        Format = SampleFormat.F32, SampleRate = 48000, Channels = 2, Layout = AudioFormat.GetLayoutFromChannels(2),
    };

    sealed class Exploding : SoundComponent
    {
        public int Calls;
        public Exploding(AudioEngine e) : base(e, Fmt) { }
        protected override void GenerateAudio(Span<float> buffer, int channels)
        {
            Calls++;
            throw new ArgumentOutOfRangeException("count", "count ('-7168') must be a non-negative value.");
        }
    }

    sealed class Tone : SoundComponent
    {
        public Tone(AudioEngine e) : base(e, Fmt) { }
        protected override void GenerateAudio(Span<float> buffer, int channels) => buffer.Fill(0.5f);
    }

    static MiniAudioEngine Engine() => new(new[] { (MiniAudioBackend)14 });

    [Fact]
    public void An_exception_inside_becomes_silence_and_one_report()
    {
        using var engine = Engine();
        var inner = new Exploding(engine);
        var faults = new List<Exception>();
        var guard = new GuardedComponent(engine, Fmt, inner, ex => { lock (faults) faults.Add(ex); });
        var buf = new float[1024];   // Process mixes into it

        guard.Process(buf, 2);
        guard.Process(buf, 2);
        SpinWait.SpinUntil(() => { lock (faults) return faults.Count > 0; }, 2000);

        buf.Should().OnlyContain(x => x == 0f);
        faults.Should().ContainSingle().Which.Should().BeOfType<ArgumentOutOfRangeException>();
        inner.Calls.Should().Be(1, "after a fault it stays quiet until it is replaced");
    }

    [Fact]
    public void Without_trouble_the_sound_passes_through()
    {
        using var engine = Engine();
        var guard = new GuardedComponent(engine, Fmt, new Tone(engine), _ => { });
        var buf = new float[1024];

        guard.Process(buf, 2);

        buf.Should().OnlyContain(x => x > 0.1f);
    }
}
