using FluentAssertions;
using StuiPodcast.Infra.Storage;
using System.Text;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

public sealed class ConfigStoreValidationTests : IDisposable
{
    private readonly string _dir;

    public ConfigStoreValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ConfigStore LoadWith(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), json, Encoding.UTF8);
        var store = new ConfigStore(_dir);
        store.Load();
        return store;
    }

    [Fact]
    public void Volume_clamped_to_0_when_negative()
    {
        var store = LoadWith("""{ "Volume0_100": -50 }""");
        store.Current.Volume0_100.Should().Be(0);
    }

    [Fact]
    public void Volume_clamped_to_100_when_above()
    {
        var store = LoadWith("""{ "Volume0_100": 999 }""");
        store.Current.Volume0_100.Should().Be(100);
    }

    [Fact]
    public void Speed_defaults_to_1_when_zero()
    {
        var store = LoadWith("""{ "Speed": 0 }""");
        store.Current.Speed.Should().Be(1.0);
    }

    [Fact]
    public void Speed_defaults_to_1_when_negative()
    {
        var store = LoadWith("""{ "Speed": -2.0 }""");
        store.Current.Speed.Should().Be(1.0);
    }

    [Fact]
    public void Speed_clamped_to_025_when_too_low()
    {
        var store = LoadWith("""{ "Speed": 0.1 }""");
        store.Current.Speed.Should().Be(0.25);
    }

    [Fact]
    public void Speed_clamped_to_4_when_too_high()
    {
        var store = LoadWith("""{ "Speed": 10.0 }""");
        store.Current.Speed.Should().Be(4.0);
    }

    [Fact]
    public void Unknown_engine_preference_falls_back_to_auto()
    {
        var store = LoadWith("""{ "EnginePreference": "banana" }""");
        store.Current.EnginePreference.Should().Be("auto");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("libvlc")]
    [InlineData("mpv")]
    [InlineData("ffplay")]
    public void Known_engine_preferences_are_preserved(string engine)
    {
        var store = LoadWith("{ \"EnginePreference\": \"" + engine + "\" }");
        store.Current.EnginePreference.Should().Be(engine);
    }

    [Fact]
    public void Unknown_theme_falls_back_to_auto()
    {
        var store = LoadWith("""{ "Theme": "neon" }""");
        store.Current.Theme.Should().Be("auto");
    }

    [Fact]
    public void Unknown_glyph_set_falls_back_to_auto()
    {
        var store = LoadWith("""{ "GlyphSet": "emojis" }""");
        store.Current.GlyphSet.Should().Be("auto");
    }

    [Fact]
    public void Empty_engine_preference_falls_back_to_auto()
    {
        var store = LoadWith("""{ "EnginePreference": "" }""");
        store.Current.EnginePreference.Should().Be("auto");
    }

    [Fact]
    public void Sort_by_unknown_key_falls_back_to_pubdate()
    {
        var store = LoadWith("""{ "ViewDefaults": { "SortBy": "bogus" } }""");
        store.Current.ViewDefaults.SortBy.Should().Be("pubdate");
    }

    [Fact]
    public void Sort_dir_unknown_falls_back_to_asc()
    {
        var store = LoadWith("""{ "ViewDefaults": { "SortDir": "zigzag" } }""");
        store.Current.ViewDefaults.SortDir.Should().Be("asc");
    }

    [Fact]
    public void Schema_version_defaults_to_1_when_zero()
    {
        var store = LoadWith("""{ "SchemaVersion": 0 }""");
        store.Current.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Corrupt_json_falls_back_to_defaults()
    {
        var store = LoadWith("not valid json at all!!!");
        store.Current.EnginePreference.Should().Be("auto");
        store.Current.Volume0_100.Should().Be(65);
        store.Current.Speed.Should().Be(1.0);
    }

    [Fact]
    public void Empty_last_selection_feed_id_defaults_to_virtual_all()
    {
        var store = LoadWith("""{ "LastSelection": { "FeedId": "" } }""");
        store.Current.LastSelection.FeedId.Should().Be("virtual:all");
    }

    [Fact]
    public void SaveNow_roundtrips_all_fields()
    {
        var store = new ConfigStore(_dir);
        store.Load();
        store.Current.Volume0_100 = 42;
        store.Current.Speed = 1.5;
        store.Current.EnginePreference = "mpv";
        store.Current.Theme = "Base";
        store.SaveNow();

        var store2 = new ConfigStore(_dir);
        store2.Load();
        store2.Current.Volume0_100.Should().Be(42);
        store2.Current.Speed.Should().Be(1.5);
        store2.Current.EnginePreference.Should().Be("mpv");
        store2.Current.Theme.Should().Be("Base");
    }
}
