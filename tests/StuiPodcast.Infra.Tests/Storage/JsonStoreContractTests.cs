using FluentAssertions;
using StuiPodcast.Infra.Storage;
using Xunit;

namespace StuiPodcast.Infra.Tests.Storage;

// The three concrete stores (ConfigStore, LibraryStore, GpodderStore) all
// share the same persistence scaffold from JsonStore<T>. These tests exercise
// that scaffold directly via a minimal TestData type so any future base-class
// regression shows up before it breaks the real stores.
public sealed class JsonStoreContractTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public JsonStoreContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-jsonstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "test.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    public sealed class TestData
    {
        public int Value { get; set; }
        public string Name { get; set; } = "";
    }

    // Concrete probe store for the scaffold.
    sealed class ProbeStore : JsonStore<TestData>
    {
        public int ValidationCalls;
        public int DefaultCalls;

        public ProbeStore(string path, TimeSpan debounce) : base(path, debounce) { }

        protected override TestData CreateDefault()
        {
            DefaultCalls++;
            return new TestData { Value = -1, Name = "default" };
        }

        protected override void ValidateAndNormalize(TestData instance)
        {
            ValidationCalls++;
            if (instance.Value < 0) instance.Value = 0;
        }
    }

    [Fact]
    public void Load_missing_file_returns_default()
    {
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        var data = store.Load();

        data.Name.Should().Be("default");
        store.DefaultCalls.Should().Be(1);
        store.ValidationCalls.Should().Be(1);
    }

    [Fact]
    public void SaveNow_writes_file_atomically()
    {
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        store.Load();
        store.Current.Value = 42;
        store.Current.Name = "roundtrip";

        store.SaveNow();

        File.Exists(_file).Should().BeTrue();
        File.Exists(_file + ".tmp").Should().BeFalse("tmp file must be cleaned after commit");
    }

    [Fact]
    public void Load_then_SaveNow_roundtrips_content()
    {
        using (var s1 = new ProbeStore(_file, TimeSpan.FromMilliseconds(50)))
        {
            s1.Load();
            s1.Current.Value = 7;
            s1.Current.Name = "roundtrip";
            s1.SaveNow();
        }

        using var s2 = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        var data = s2.Load();

        data.Value.Should().Be(7);
        data.Name.Should().Be("roundtrip");
    }

    [Fact]
    public void Corrupt_file_falls_back_to_default()
    {
        File.WriteAllText(_file, "this isn't valid json }}}");
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        var data = store.Load();

        data.Name.Should().Be("default");
    }

    [Fact]
    public void Validate_clamps_loaded_value()
    {
        File.WriteAllText(_file, """{"Value": -5, "Name": "x"}""");
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        var data = store.Load();

        data.Value.Should().Be(0, "ValidateAndNormalize should have clamped -5 to 0");
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        File.WriteAllText(_file, """
        {
          // a comment
          "Value": 3,
          "Name": "hi",
        }
        """);
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        var data = store.Load();

        data.Value.Should().Be(3);
        data.Name.Should().Be("hi");
    }

    [Fact]
    public void Orphaned_tmp_file_is_cleaned_on_Load()
    {
        File.WriteAllText(_file + ".tmp", "garbage");
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        store.Load();

        File.Exists(_file + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Changed_event_fires_on_SaveNow()
    {
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        store.Load();

        int fires = 0;
        store.Changed += () => fires++;
        store.Current.Value = 5;
        store.SaveNow();

        fires.Should().Be(1);
    }

    [Fact]
    public async Task Debounced_SaveAsync_coalesces_bursts()
    {
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(100));
        store.Load();

        int fires = 0;
        store.Changed += () => fires++;

        // Fire 20 rapid save requests; the debouncer should coalesce them.
        for (int i = 0; i < 20; i++)
        {
            store.Current.Value = i;
            store.SaveAsync();
        }

        await Task.Delay(300);
        fires.Should().BeGreaterThan(0);
        fires.Should().BeLessThanOrEqualTo(3, "rapid bursts must not cause 20 file writes");

        // Verify the last value actually persisted.
        using var s2 = new ProbeStore(_file, TimeSpan.FromMilliseconds(100));
        s2.Load().Value.Should().Be(19);
    }

    [Fact]
    public void Dispose_flushes_pending_save()
    {
        var store = new ProbeStore(_file, TimeSpan.FromSeconds(5));
        store.Load();
        store.Current.Value = 99;
        store.SaveAsync();
        // Don't wait for the long debounce — Dispose must flush.
        store.Dispose();

        using var s2 = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        s2.Load().Value.Should().Be(99);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        store.Load();
        store.Dispose();
        var act = () => store.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void FilePath_and_TmpPath_are_exposed()
    {
        using var store = new ProbeStore(_file, TimeSpan.FromMilliseconds(50));
        store.FilePath.Should().Be(_file);
        store.TmpPath.Should().Be(_file + ".tmp");
    }
}
