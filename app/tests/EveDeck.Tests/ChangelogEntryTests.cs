using EveDeck.Services;
using EveDeck.ViewModels;
using Xunit;

namespace EveDeck.Tests;

// The "What's New" tiles expand in place rather than sending the user to a browser, so the split
// between the collapsed summary and the full notes -- and whether there is anything to expand at
// all -- has to be right, or a tile offers "Show more" that reveals nothing.
public class ChangelogEntryTests
{
    // A real release body: the hand-written bullets, then the boilerplate release.yml appends.
    private const string RealBody = """
- First fix, the interesting one
- Second fix

**Two ways to install:**
- **EveDeck-Setup-v1.43.0.exe** - normal Windows installer
---
**VirusTotal**
- portable: clean
""";

    [Fact]
    public void CleanBody_StripsTheAppendedBoilerplate()
    {
        var cleaned = ChangelogService.CleanBody(RealBody);

        Assert.Contains("First fix, the interesting one", cleaned);
        Assert.Contains("Second fix", cleaned);
        Assert.DoesNotContain("Two ways to install", cleaned);
        Assert.DoesNotContain("VirusTotal", cleaned);
        Assert.DoesNotContain("EveDeck-Setup", cleaned);
    }

    [Fact]
    public void Summarize_LeavesAShortBodyExactlyAsItIs()
    {
        var cleaned = ChangelogService.CleanBody(RealBody);

        // Identity is what tells the UI there is nothing to expand -- see IsTruncated at the call site.
        Assert.Equal(cleaned, ChangelogService.Summarize(cleaned));
    }

    [Fact]
    public void Summarize_ClipsALongBodyAndMarksIt()
    {
        var longBody = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"- Bullet number {i} with some padding text"));

        var summary = ChangelogService.Summarize(longBody);

        Assert.NotEqual(longBody, summary);
        Assert.True(summary.Length < longBody.Length);
        Assert.EndsWith("...", summary);
    }

    // -- Tile behaviour ------------------------------------------------------------------------

    private static ChangelogEntryViewModel Entry(string summary, string full, bool truncated) =>
        new(new ChangelogService.ReleaseNote("1.43.1", summary, full, truncated, "https://example.invalid", null));

    [Fact]
    public void Tile_ShowsTheSummaryUntilItIsExpanded()
    {
        var vm = Entry("short version...", "the whole thing", truncated: true);

        Assert.False(vm.IsExpanded);
        Assert.Equal("short version...", vm.BodyText);

        vm.ToggleCommand.Execute(null);

        Assert.True(vm.IsExpanded);
        Assert.Equal("the whole thing", vm.BodyText);
    }

    [Fact]
    public void Tile_CollapsesAgainOnASecondClick()
    {
        var vm = Entry("short version...", "the whole thing", truncated: true);

        vm.ToggleCommand.Execute(null);
        vm.ToggleCommand.Execute(null);

        Assert.False(vm.IsExpanded);
        Assert.Equal("short version...", vm.BodyText);
    }

    // A release whose notes already fit shows no affordance, and clicking it must not toggle to an
    // identical body while flipping the label to "Show less".
    [Fact]
    public void Tile_DoesNothingWhenThereIsNothingToExpand()
    {
        var vm = Entry("all of it", "all of it", truncated: false);

        Assert.False(vm.CanExpand);

        vm.ToggleCommand.Execute(null);

        Assert.False(vm.IsExpanded);
        Assert.Equal("all of it", vm.BodyText);
    }

    [Fact]
    public void Tile_LabelFollowsTheExpandedState()
    {
        var vm = Entry("short...", "long", truncated: true);

        Assert.Contains("Show more", vm.ToggleText);
        vm.ToggleCommand.Execute(null);
        Assert.Contains("Show less", vm.ToggleText);
    }

    [Fact]
    public void Tile_RaisesChangeNotificationsForTheBoundText()
    {
        var vm = Entry("short...", "long", truncated: true);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleCommand.Execute(null);

        Assert.Contains(nameof(vm.BodyText), changed);
        Assert.Contains(nameof(vm.ToggleText), changed);
    }
}
