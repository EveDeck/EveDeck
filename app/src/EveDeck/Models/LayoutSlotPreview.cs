namespace EveDeck.Models;

public sealed class LayoutSlotPreview
{
    public int SlotNumber { get; set; }
    public string DisplayText { get; set; } = "";
    public string Label { get; set; } = "";

    // The master rect (the biggest slot -- see MainWindowViewModel.PickCenterSlot). Drawn differently
    // in the Layout Preview so "which one is the master" is answerable at a glance instead of being
    // inferred from a table of coordinates.
    public bool IsMaster { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
