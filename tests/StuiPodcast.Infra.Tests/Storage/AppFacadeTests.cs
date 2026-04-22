using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

// AppFacade is a thin wrapper around ConfigStore + LibraryStore, but it's the
// single entry-point used by everything. Basic wiring tests lock in the
// contract: mutations flow through to the stores, changes fire events, and
// dispose tears down cleanly.
public sealed class AppFacadeTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigStore _config;
    private readonly LibraryStore _library;
    private readonly AppFacade _facade;

    public AppFacadeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-facade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigStore(_dir);
        _library = new LibraryStore(_dir);
        _config.Load();
        _library.Load();
        _facade = new AppFacade(_config, _library);
    }

    public void Dispose()
    {
        try { _facade.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Throws_on_null_config_store()
    {
        var act = () => new AppFacade(null!, _library);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Throws_on_null_library_store()
    {
        var act = () => new AppFacade(_config, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EnginePreference_roundtrips_through_facade()
    {
        _facade.EnginePreference = "mpv";
        _facade.EnginePreference.Should().Be("mpv");
        _config.Current.EnginePreference.Should().Be("mpv");
    }

    [Fact]
    public void Volume_roundtrips_through_facade()
    {
        _facade.Volume0100 = 42;
        _facade.Volume0100.Should().Be(42);
    }

    [Fact]
    public void Null_engine_preference_falls_back_to_auto()
    {
        _facade.EnginePreference = null;
        _facade.EnginePreference.Should().Be("auto");
    }

    [Fact]
    public void AddOrUpdateFeed_persists_through_library()
    {
        var feed = new Feed { Title = "T", Url = "https://x.com/feed" };
        var saved = _facade.AddOrUpdateFeed(feed);

        saved.Id.Should().NotBe(Guid.Empty);
        _facade.Feeds.Should().ContainSingle(f => f.Id == saved.Id);
    }

    [Fact]
    public void Feeds_view_is_readonly()
    {
        var feed = new Feed { Title = "T", Url = "https://x.com/f.xml" };
        _facade.AddOrUpdateFeed(feed);

        var view = _facade.Feeds;
        view.Should().BeAssignableTo<System.Collections.Generic.IReadOnlyList<Feed>>();
    }

    [Fact]
    public void Changed_event_fires_on_config_save()
    {
        int fires = 0;
        _facade.Changed += () => fires++;

        _config.Current.Volume0_100 = 50;
        _config.SaveNow();

        fires.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Changed_event_fires_on_library_save()
    {
        int fires = 0;
        _facade.Changed += () => fires++;

        _library.AddOrUpdateFeed(new Feed { Title = "T", Url = "https://x.com/rss" });
        _library.SaveNow();

        fires.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Dispose_disposes_both_stores()
    {
        _facade.Dispose();

        // Using the stores after dispose should not throw but should not persist anymore.
        // LibraryStore's SaveAsync after dispose: the timer is released, so this is a no-op.
        var act = () => _library.SaveAsync();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        _facade.Dispose();
        var act = () => _facade.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsDownloaded_with_null_lookup_returns_false()
    {
        _facade.IsDownloaded(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void TryGetLocalPath_with_null_lookup_returns_false()
    {
        _facade.TryGetLocalPath(Guid.NewGuid(), out var path).Should().BeFalse();
        path.Should().BeNull();
    }
}
