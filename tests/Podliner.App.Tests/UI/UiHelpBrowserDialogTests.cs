using FluentAssertions;
using Podliner.App.UI;
using Terminal.Gui;
using Xunit;

namespace Podliner.App.Tests.UI;

// The help browser is the only screen a new user is told to open (":h"), so a
// control that is drawn but invisible is worse here than anywhere else.
[Collection(TuiCollection.Name)]
public sealed class UiHelpBrowserDialogTests
{
    // The dialog asks for 100x32, so give it room plus the shell around it.
    private static TuiHarness NewHarness() => new(120, 40);

    [Fact]
    public void Keys_tab_shows_its_search_label()
    {
        using var h = NewHarness();
        var parts = UiHelpBrowserDialog.Build();

        h.Render(parts.Dialog);

        h.ScreenContains("Search:").Should().BeTrue(
            "the keys tab has a search box and nothing else labels it:\n" + h.Screen());
    }

    [Fact]
    public void Commands_tab_shows_its_search_label_next_to_the_category_label()
    {
        using var h = NewHarness();
        var parts = UiHelpBrowserDialog.Build();
        h.Render(parts.Dialog);

        parts.Tabs.SelectedTab = parts.CommandsTab;
        h.Render(parts.Dialog);

        h.ScreenContains("Search:").Should().BeTrue("the commands tab has a search box too:\n" + h.Screen());
        h.ScreenContains("Category:").Should().BeTrue(h.Screen());

        // Both labels sit in the tab header area, one row apart.
        (h.RowOf("Category:") - h.RowOf("Search:")).Should().Be(1);
    }

    [Fact]
    public void Search_field_does_not_start_underneath_its_label()
    {
        using var h = NewHarness();
        var parts = UiHelpBrowserDialog.Build();
        h.Render(parts.Dialog);

        foreach (var field in FindSearchFields(parts.Dialog))
            field.Frame.X.Should().BeGreaterThan("Search:".Length,
                "a TextField drawn over the label blanks it out");
    }

    private static IEnumerable<TextField> FindSearchFields(View root)
    {
        foreach (var child in root.Subviews)
        {
            if (child is TextField tf) yield return tf;
            foreach (var nested in FindSearchFields(child)) yield return nested;
        }
    }
}
