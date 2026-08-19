using Xunit;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.ViewModels;

namespace EveDeck.Tests;

// The "master on my main monitor, previews on my tablet" arrangement. It is the setup people ask
// about most and the one they cannot build by hand, because the master rect is chosen by GEOMETRY
// (biggest slot wins) and nothing said so -- four equal rects across two screens put the master
// wherever the tie-break landed. These tests pin both halves of the fix: the generator produces a
// dominant master, and the ambiguity check flags layouts where it isn't.
public class MasterPreviewLayoutTests
{
    private static WindowRect Rect(int x, int y, int w, int h) =>
        new() { X = x, Y = y, Width = w, Height = h };

    private static LayoutSlot Slot(int n, int x, int y, int w, int h) =>
        new() { SlotNumber = n, X = x, Y = y, Width = w, Height = h };

    // A 1920x1200 main monitor with a 1280x800 tablet parked below it, which is the shape of the
    // spacedesk setups this was built for.
    private static readonly WindowRect MainMonitor = Rect(0, 0, 1920, 1200);
    private static readonly WindowRect Tablet = Rect(0, 1200, 1280, 800);

    [Fact]
    public void MasterSlotFillsTheMasterMonitor()
    {
        var profile = PresetFactory.CreateMasterPreviewProfile(
            "Master + Previews", "main", MainMonitor, "tablet", Tablet, 4);

        var master = profile.Slots.Single(s => s.SlotNumber == 1);
        Assert.Equal(0, master.X);
        Assert.Equal(0, master.Y);
        Assert.Equal(1920, master.Width);
        Assert.Equal(1200, master.Height);
        Assert.Equal("main", master.MonitorId);
    }

    [Fact]
    public void GeneratedMasterIsUnambiguouslyTheBiggestSlot()
    {
        var profile = PresetFactory.CreateMasterPreviewProfile(
            "Master + Previews", "main", MainMonitor, "tablet", Tablet, 4);

        Assert.Equal(1, MainWindowViewModel.PickCenterSlot(profile.Slots));
        Assert.False(MainWindowViewModel.IsMasterAmbiguous(profile.Slots));
    }

    [Fact]
    public void PreviewSlotsTileTheSecondMonitorAndStayInsideIt()
    {
        var profile = PresetFactory.CreateMasterPreviewProfile(
            "Master + Previews", "main", MainMonitor, "tablet", Tablet, 4);

        var previews = profile.Slots.Where(s => s.SlotNumber > 1).ToList();
        Assert.Equal(3, previews.Count);
        Assert.All(previews, s => Assert.Equal("tablet", s.MonitorId));
        Assert.All(previews, s =>
        {
            Assert.InRange(s.X, Tablet.X, Tablet.X + Tablet.Width);
            Assert.InRange(s.Y, Tablet.Y, Tablet.Y + Tablet.Height);
            Assert.InRange(s.X + s.Width, Tablet.X, Tablet.X + Tablet.Width);
            Assert.InRange(s.Y + s.Height, Tablet.Y, Tablet.Y + Tablet.Height);
        });
    }

    [Fact]
    public void ProfileSupportsPreviewModeAndDesignatesAMasterSeat()
    {
        var profile = PresetFactory.CreateMasterPreviewProfile(
            "Master + Previews", "main", MainMonitor, "tablet", Tablet, 4);

        Assert.True(profile.SupportsCornerGrid); // otherwise it would silently fall back to flat mode
        Assert.Equal(1, profile.MasterSeat);
        Assert.Equal("Custom", profile.Category);
        Assert.False(profile.IsBuiltIn);
    }

    [Fact]
    public void SingleSeatProducesJustTheMaster()
    {
        var profile = PresetFactory.CreateMasterPreviewProfile(
            "Solo", "main", MainMonitor, "tablet", Tablet, 1);

        Assert.Single(profile.Slots);
        Assert.Equal(1, profile.Slots[0].SlotNumber);
    }

    [Fact]
    public void FourEqualSlotsAcrossTwoScreensAreFlaggedAmbiguous()
    {
        // Exactly the layout a user builds by hand and then reports as "master won't move to my
        // main monitor": four identical rects, nothing dominant.
        var slots = new[]
        {
            Slot(1, 0, 1200, 640, 400),
            Slot(2, 640, 1200, 640, 400),
            Slot(3, 0, 1600, 640, 400),
            Slot(4, 640, 1600, 640, 400),
        };

        Assert.True(MainWindowViewModel.IsMasterAmbiguous(slots));
    }

    [Fact]
    public void UniformGridRemainderStillCountsAsAmbiguous()
    {
        // PopulateGridSlots hands the last column/row the integer-division remainder, so a "uniform"
        // grid is a few pixels off uniform. That must not read as a genuine dominant master.
        var profile = PresetFactory.CreateCustomProfile("Grid", 2560, 1440, 9);
        Assert.True(MainWindowViewModel.IsMasterAmbiguous(profile.Slots));
    }

    [Fact]
    public void ADominantMasterIsNotAmbiguous()
    {
        var slots = new[]
        {
            Slot(1, 0, 0, 1920, 1200),
            Slot(2, 0, 1200, 640, 400),
            Slot(3, 640, 1200, 640, 400),
        };

        Assert.False(MainWindowViewModel.IsMasterAmbiguous(slots));
    }

    [Fact]
    public void SingleSlotLayoutIsNeverAmbiguous()
    {
        Assert.False(MainWindowViewModel.IsMasterAmbiguous(new[] { Slot(1, 0, 0, 1920, 1200) }));
        Assert.False(MainWindowViewModel.IsMasterAmbiguous(Array.Empty<LayoutSlot>()));
    }
}
