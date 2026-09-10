using FluentAssertions;
using StuiPodcast.App;
using StuiPodcast.App.Command;
using Xunit;

namespace StuiPodcast.App.Tests;

// The help browser (`:h`) is the only in-app documentation. Its worst failure
// mode is quiet drift: a command gets added or renamed and the catalog keeps
// describing the old one, or documents something the parser never accepted.
// These tests tie the catalog to the parser so that drift breaks the build.
public sealed class HelpCatalogTests
{
    // ── catalog hygiene ─────────────────────────────────────────────────────

    [Fact]
    public void There_are_commands_and_keys_documented()
    {
        HelpCatalog.Commands.Should().NotBeEmpty();
        HelpCatalog.Keys.Should().NotBeEmpty();
    }

    [Fact]
    public void Every_command_entry_starts_with_a_colon()
        => HelpCatalog.Commands.Should().OnlyContain(c => c.Command.StartsWith(":"));

    [Fact]
    public void No_command_is_documented_twice()
        => HelpCatalog.Commands.Select(c => c.Command)
            .Should().OnlyHaveUniqueItems();

    [Fact]
    public void Every_command_has_a_description()
        => HelpCatalog.Commands.Should()
            .OnlyContain(c => !string.IsNullOrWhiteSpace(c.Description));

    [Fact]
    public void Every_key_has_a_description()
        => HelpCatalog.Keys.Should()
            .OnlyContain(k => !string.IsNullOrWhiteSpace(k.Key) && !string.IsNullOrWhiteSpace(k.Description));

    [Fact]
    public void No_alias_is_claimed_by_two_commands()
    {
        var aliases = HelpCatalog.Commands
            .SelectMany(c => c.Aliases ?? Array.Empty<string>())
            .ToList();

        aliases.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void No_alias_collides_with_a_command_name()
    {
        var names = HelpCatalog.Commands.Select(c => c.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = HelpCatalog.Commands.SelectMany(c => c.Aliases ?? Array.Empty<string>());

        aliases.Should().OnlyContain(a => !names.Contains(a));
    }

    // ── the catalog must match what the app accepts ─────────────────────────

    // Queue and download verbs are handled by fastpaths in CmdRouter before
    // the parser ever sees them, so "does the parser know it" is not the same
    // question as "does the app accept it".
    private static bool AppAccepts(string raw)
        => CmdParser.Parse(raw).Kind != TopCommand.Unknown
           || raw.StartsWith(":dl", StringComparison.OrdinalIgnoreCase)
           || raw.StartsWith(":download", StringComparison.OrdinalIgnoreCase)
           || raw.StartsWith(":queue", StringComparison.OrdinalIgnoreCase)
           || raw.Equals("q", StringComparison.OrdinalIgnoreCase);

    // A few entries deliberately document several commands at once
    // (":zt / :zz / :zb"), so their aliases cannot all map to one TopCommand.
    private static bool IsCompoundEntry(CmdHelp c) => c.Command.Contains('/');

    [Fact]
    public void Every_documented_command_is_understood_by_the_parser()
    {
        var unknown = HelpCatalog.Commands
            .Where(c => !AppAccepts(c.Command))
            .Select(c => c.Command)
            .ToList();

        unknown.Should().BeEmpty(
            "the help browser must not document commands the parser rejects");
    }

    [Fact]
    public void Every_documented_alias_is_understood_by_the_parser()
    {
        var unknown = HelpCatalog.Commands
            .SelectMany(c => (c.Aliases ?? Array.Empty<string>()).Select(a => (c.Command, Alias: a)))
            .Where(x => !AppAccepts(x.Alias))
            .Select(x => $"{x.Alias} (for {x.Command})")
            .ToList();

        unknown.Should().BeEmpty();
    }

    [Fact]
    public void An_alias_resolves_to_the_same_command_it_documents()
    {
        // Guards against documenting an alias that quietly runs something
        // else: ":queue" once listed "q", which is the quit key.
        foreach (var c in HelpCatalog.Commands)
        {
            if (IsCompoundEntry(c)) continue;
            var expected = CmdParser.Parse(c.Command).Kind;
            if (expected == TopCommand.Unknown) continue;   // router fastpath

            foreach (var alias in c.Aliases ?? Array.Empty<string>())
                CmdParser.Parse(alias).Kind.Should().Be(expected,
                    $"{alias} is documented as an alias of {c.Command}");
        }
    }

    [Fact]
    public void Every_documented_example_is_understood_by_the_parser()
    {
        var unknown = HelpCatalog.Commands
            .SelectMany(c => c.Examples ?? Array.Empty<string>())
            .Where(ex => !AppAccepts(ex))
            .ToList();

        unknown.Should().BeEmpty(
            "a copied example that the parser rejects is worse than no example");
    }

    [Fact]
    public void An_example_invokes_the_command_it_sits_under()
    {
        foreach (var c in HelpCatalog.Commands)
        {
            if (IsCompoundEntry(c)) continue;
            var expected = CmdParser.Parse(c.Command).Kind;
            if (expected == TopCommand.Unknown) continue;   // router fastpath

            foreach (var ex in c.Examples ?? Array.Empty<string>())
                CmdParser.Parse(ex).Kind.Should().Be(expected,
                    $"example \"{ex}\" is listed under {c.Command}");
        }
    }

    // ── ordering helpers ────────────────────────────────────────────────────

    [Fact]
    public void MostUsed_returns_the_lowest_ranked_entries()
    {
        var most = HelpCatalog.MostUsed(5).ToList();

        most.Should().HaveCount(5);
        most.Should().BeInAscendingOrder(c => c.Rank);
        most.Max(c => c.Rank).Should().BeLessThanOrEqualTo(
            HelpCatalog.Commands.OrderBy(c => c.Rank).Skip(4).First().Rank);
    }

    [Fact]
    public void MostUsed_never_returns_more_than_exists()
        => HelpCatalog.MostUsed(10_000).Should().HaveCount(HelpCatalog.Commands.Count);

    [Fact]
    public void MostUsed_of_zero_is_empty()
        => HelpCatalog.MostUsed(0).Should().BeEmpty();

    [Fact]
    public void Grouping_by_category_keeps_every_command()
        => HelpCatalog.GroupedByCategory().SelectMany(g => g)
            .Should().HaveCount(HelpCatalog.Commands.Count);

    [Fact]
    public void Each_group_is_sorted_by_command_name()
    {
        foreach (var group in HelpCatalog.GroupedByCategory())
            group.Select(c => c.Command)
                 .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    // ── long-form docs ──────────────────────────────────────────────────────

    [Fact]
    public void The_long_form_docs_are_filled_in()
    {
        HelpCatalog.EngineDoc.Should().NotBeNullOrWhiteSpace();
        HelpCatalog.OpmlDoc.Should().NotBeNullOrWhiteSpace();
        HelpCatalog.SyncDoc.Should().NotBeNullOrWhiteSpace();
    }
}
