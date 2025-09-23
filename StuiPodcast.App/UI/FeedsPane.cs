using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using StuiPodcast.Core;

namespace StuiPodcast.App.UI;

internal sealed class FeedsPane
{
    public FrameView Frame { get; }
    public ListView  List  { get; }

    private List<Feed> _feeds = new();

    public event Action? SelectedChanged;
    public event Action? OpenRequested;

    public FeedsPane()
    {
        Frame = new FrameView("Feeds") { X = 0, Y = 0, Width = 30, Height = Dim.Fill() };
        List  = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        List.OpenSelectedItem += _ => OpenRequested?.Invoke();
        List.SelectedItemChanged += _ => SelectedChanged?.Invoke();

        Frame.Add(List);
    }

    public void SetFeeds(IEnumerable<Feed> feeds) 
    {
        _feeds = (feeds ?? Enumerable.Empty<Feed>()).ToList();
        List.SetSource(_feeds.Select(f => f.Title).ToList());
    }

    public Guid? GetSelectedFeedId()
    {
        if (_feeds.Count == 0 || List.Source is null) return null;
        int i = Math.Clamp(List.SelectedItem, 0, _feeds.Count - 1);
        return _feeds.ElementAtOrDefault(i)?.Id;
    }

    public void SelectFeed(Guid id)
    {
        if (_feeds.Count == 0) return;
        var idx = _feeds.FindIndex(f => f.Id == id);
        if (idx >= 0) List.SelectedItem = idx;
    }

    public IReadOnlyList<Feed> RawFeeds => _feeds;
}