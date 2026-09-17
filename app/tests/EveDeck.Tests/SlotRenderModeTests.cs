using Xunit;
using EveDeck.Models;
using EveDeck.ViewModels;

namespace EveDeck.Tests;

// LayoutSlot.RenderMode lets ONE layout mix real client windows and live preview tiles. The profile
// level flag could not express that: flat mode turns every slot into a window (and EVE clamps
// anything under 1024x768, so small slots overlap), while preview mode demotes every non-master slot
// to a capture -- including full-monitor ones, which is both blurrier and far costlier than just
// placing the window there.
//
// These pin the resolution rule, including the back-compat case: a slot saved before this field
// existed has RenderMode == "" and must behave exactly as it always did.
public class SlotRenderModeTests
{
    private const int Center = 1;

    private static LayoutSlot Slot(int n, string mode = "") =>
        new() { SlotNumber = n, X = 0, Y = 0, Width = 800, Height = 600, RenderMode = mode };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MasterSlotIsAlwaysARealWindow(bool previewModeActive)
    {
        // Even explicitly marked "Preview": a preview of the master would be a picture of a window
        // that is not there, because the master is the client the user actually flies.
        var master = Slot(Center, "Preview");
        Assert.True(MainWindowViewModel.SlotRendersAsWindow(master, previewModeActive, Center));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitWindowModeWinsInEitherProfileMode(bool previewModeActive)
    {
        Assert.True(MainWindowViewModel.SlotRendersAsWindow(Slot(2, "Window"), previewModeActive, Center));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitPreviewModeWinsInEitherProfileMode(bool previewModeActive)
    {
        Assert.False(MainWindowViewModel.SlotRendersAsWindow(Slot(2, "Preview"), previewModeActive, Center));
    }

    // Back-compat: every slot saved before RenderMode existed carries "".
    [Fact]
    public void EmptyRenderModeInheritsPreviewMode()
    {
        Assert.False(MainWindowViewModel.SlotRendersAsWindow(Slot(2), previewModeActive: true, Center));
    }

    [Fact]
    public void EmptyRenderModeInheritsFlatMode()
    {
        Assert.True(MainWindowViewModel.SlotRendersAsWindow(Slot(2), previewModeActive: false, Center));
    }

    // An unknown token must degrade to inherit rather than throw or silently pick a side -- the field
    // is a plain string in settings.json and a typo should not break a layout.
    [Theory]
    [InlineData("window")]
    [InlineData("WINDOW")]
    public void WindowTokenIsCaseInsensitive(string mode)
    {
        Assert.True(MainWindowViewModel.SlotRendersAsWindow(Slot(2, mode), previewModeActive: true, Center));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("PREVIEW")]
    public void PreviewTokenIsCaseInsensitive(string mode)
    {
        Assert.False(MainWindowViewModel.SlotRendersAsWindow(Slot(2, mode), previewModeActive: false, Center));
    }

    [Fact]
    public void UnknownTokenInherits()
    {
        Assert.False(MainWindowViewModel.SlotRendersAsWindow(Slot(2, "wat"), previewModeActive: true, Center));
        Assert.True(MainWindowViewModel.SlotRendersAsWindow(Slot(2, "wat"), previewModeActive: false, Center));
    }

    // The layout that motivated the feature: three full-size windows across three monitors, plus two
    // small previews tucked on the main one, all inside a preview-mode profile.
    [Fact]
    public void MixedLayoutResolvesThreeWindowsAndTwoPreviews()
    {
        LayoutSlot[] slots =
        [
            Slot(1),                 // master, on the 32" -- always a window
            Slot(2, "Window"),       // second monitor, full size
            Slot(3, "Window"),       // tablet, full size
            Slot(4, "Preview"),      // small tile
            Slot(5, "Preview"),      // small tile
        ];

        var windows = slots.Where(s => MainWindowViewModel.SlotRendersAsWindow(s, previewModeActive: true, Center))
                           .Select(s => s.SlotNumber)
                           .ToArray();

        Assert.Equal([1, 2, 3], windows);
    }

    [Fact]
    public void RenderModeDisplayRoundTrips()
    {
        var slot = new LayoutSlot();
        Assert.Equal("Follow layout", slot.RenderModeDisplay);

        slot.RenderModeDisplay = "Real window";
        Assert.Equal("Window", slot.RenderMode);
        Assert.Equal("Real window", slot.RenderModeDisplay);

        slot.RenderModeDisplay = "Live preview";
        Assert.Equal("Preview", slot.RenderMode);
        Assert.Equal("Live preview", slot.RenderModeDisplay);

        slot.RenderModeDisplay = "Follow layout";
        Assert.Equal("", slot.RenderMode);
    }

    // An unrecognised stored value must READ BACK as "Follow layout" so the combo box never disagrees
    // with the rule SlotRendersAsWindow actually applies to it.
    [Fact]
    public void RenderModeDisplayShowsUnknownStoredValueAsFollowLayout()
    {
        Assert.Equal("Follow layout", new LayoutSlot { RenderMode = "wat" }.RenderModeDisplay);
    }

    [Fact]
    public void CloneCarriesRenderMode()
    {
        var profile = new LayoutProfile();
        profile.Slots.Add(Slot(1, "Window"));
        profile.Slots.Add(Slot(2, "Preview"));
        profile.Slots.Add(Slot(3));

        var clone = profile.Clone();

        Assert.Equal("Window", clone.Slots[0].RenderMode);
        Assert.Equal("Preview", clone.Slots[1].RenderMode);
        Assert.Equal("", clone.Slots[2].RenderMode);
    }
}
