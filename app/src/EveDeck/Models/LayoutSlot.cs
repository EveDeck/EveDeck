namespace EveDeck.Models;

public sealed class LayoutSlot
{
    public int SlotNumber { get; set; }
    public string Label { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Borderless { get; set; } = true;

    // The seat (SlotAssignment.SlotNumber) that occupies THIS position at rest in corner/grid mode.
    // null = auto-derive (legacy: position number == seat number, with leftover fallback). Set by the
    // user dragging a seat card onto a mini-map corner; ignored for the center slot (master sits there).
    public int? HomeSeat { get; set; }

    // Display-only marker for the slot table: true for whichever slot is currently the master rect.
    // NOT persisted and never read back as truth -- the master is always re-derived from geometry by
    // MainWindowViewModel.PickCenterSlot, and this is only stamped on for the UI to show it.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMasterSlot { get; set; }

    public WindowRect ToRect() => new() { X = X, Y = Y, Width = Width, Height = Height };
}
