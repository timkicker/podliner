using System.Text.Json;
using FluentAssertions;
using StuiPodcast.Core;
using StuiPodcast.Infra.Download;
using Xunit;

namespace StuiPodcast.Infra.Tests.Download;

public sealed class DownloadIndexStoreTests : IDisposable
{
    private readonly string _dir;

    public DownloadIndexStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "podliner-dlidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_dir, "downloads.json");

    private string MakeLocalFile(string name = "ep.mp3")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    private static DownloadIndex.Item Item(Guid id, string path)
        => new() { EpisodeId = id, LocalPath = path };

    private List<(Guid Id, DownloadStatus Status)> LoadInto(DownloadIndexStore store)
    {
        var restored = new List<(Guid, DownloadStatus)>();
        store.Load((id, st) => restored.Add((id, st)));
        return restored;
    }

    // ── load ────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_returns_zero_when_the_index_is_missing()
    {
        using var store = new DownloadIndexStore(_dir);

        store.Load((_, _) => throw new Exception("must not be called")).Should().Be(0);
    }

    [Fact]
    public void Load_restores_an_entry_whose_file_still_exists()
    {
        var id = Guid.NewGuid();
        var file = MakeLocalFile();
        using var store = new DownloadIndexStore(_dir);
        store.SaveNow(() => new[] { Item(id, file) });

        using var reopened = new DownloadIndexStore(_dir);
        var restored = LoadInto(reopened);

        restored.Should().ContainSingle();
        restored[0].Id.Should().Be(id);
        restored[0].Status.State.Should().Be(DownloadState.Done);
        restored[0].Status.LocalPath.Should().Be(file);
    }

    [Fact]
    public void Load_skips_an_entry_whose_file_was_deleted()
    {
        var id = Guid.NewGuid();
        var file = MakeLocalFile();
        using var store = new DownloadIndexStore(_dir);
        store.SaveNow(() => new[] { Item(id, file) });

        // The user cleaned up the podcast folder outside the app.
        File.Delete(file);

        using var reopened = new DownloadIndexStore(_dir);
        LoadInto(reopened).Should().BeEmpty();
    }

    [Fact]
    public void Load_skips_entries_with_an_empty_id_or_path()
    {
        var good = Guid.NewGuid();
        var file = MakeLocalFile();
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(() => new[]
        {
            Item(Guid.Empty, file),
            Item(Guid.NewGuid(), ""),
            Item(Guid.NewGuid(), "   "),
            Item(good, file),
        });

        using var reopened = new DownloadIndexStore(_dir);
        var restored = LoadInto(reopened);

        restored.Should().ContainSingle();
        restored[0].Id.Should().Be(good);
    }

    [Fact]
    public void Load_survives_a_corrupt_index()
    {
        File.WriteAllText(IndexPath, "{ this is not json");
        using var store = new DownloadIndexStore(_dir);

        var act = () => store.Load((_, _) => { });

        act.Should().NotThrow();
        store.Load((_, _) => { }).Should().Be(0);
    }

    [Fact]
    public void Load_survives_an_empty_index_file()
    {
        File.WriteAllText(IndexPath, "");
        using var store = new DownloadIndexStore(_dir);

        store.Load((_, _) => { }).Should().Be(0);
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        var id = Guid.NewGuid();
        var file = MakeLocalFile();
        File.WriteAllText(IndexPath, $$"""
            {
              // hand-edited
              "SchemaVersion": 1,
              "Items": [
                { "EpisodeId": "{{id}}", "LocalPath": {{JsonSerializer.Serialize(file)}} },
              ],
            }
            """);

        using var store = new DownloadIndexStore(_dir);
        var restored = LoadInto(store);

        restored.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    // ── save ────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveNow_writes_the_index_file()
    {
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(() => new[] { Item(Guid.NewGuid(), MakeLocalFile()) });

        File.Exists(IndexPath).Should().BeTrue();
        File.ReadAllText(IndexPath).Should().Contain("SchemaVersion");
    }

    [Fact]
    public void SaveNow_leaves_no_temp_file_behind()
    {
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(() => new[] { Item(Guid.NewGuid(), MakeLocalFile()) });

        File.Exists(IndexPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void SaveNow_overwrites_an_existing_index()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var file = MakeLocalFile();
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(() => new[] { Item(first, file) });
        store.SaveNow(() => new[] { Item(second, file) });

        using var reopened = new DownloadIndexStore(_dir);
        var restored = LoadInto(reopened);

        restored.Should().ContainSingle().Which.Id.Should().Be(second);
    }

    [Fact]
    public void SaveNow_can_write_an_empty_index()
    {
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(Array.Empty<DownloadIndex.Item>);

        File.Exists(IndexPath).Should().BeTrue();
        using var reopened = new DownloadIndexStore(_dir);
        LoadInto(reopened).Should().BeEmpty();
    }

    [Fact]
    public void A_saved_index_round_trips_every_entry()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var files = ids.Select((_, i) => MakeLocalFile($"ep{i}.mp3")).ToArray();
        using var store = new DownloadIndexStore(_dir);

        store.SaveNow(() => ids.Select((id, i) => Item(id, files[i])).ToList());

        using var reopened = new DownloadIndexStore(_dir);
        LoadInto(reopened).Select(r => r.Id).Should().BeEquivalentTo(ids);
    }

    // ── dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_safe_without_a_pending_save()
    {
        var store = new DownloadIndexStore(_dir);

        var act = () => store.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_twice_is_safe()
    {
        var store = new DownloadIndexStore(_dir);
        store.SaveDebounced(Array.Empty<DownloadIndex.Item>);

        store.Dispose();
        var act = () => store.Dispose();

        act.Should().NotThrow();
    }
}
