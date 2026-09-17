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

    // Per-slot override of LayoutProfile.PreviewModeOverride, so one layout can mix real windows and
    // live previews:
    //   ""        -> inherit the profile's mode (every profile saved before this field behaves exactly
    //                as it did: window in flat mode, preview in preview mode).
    //   "Window"  -> this slot always holds a REAL client window at its own rect.
    //   "Preview" -> this slot is always a thumbnail; its client parks with the other preview seats.
    //
    // The profile-level flag alone could not express the layout that motivated this: three clients at
    // full size across three monitors PLUS two small previews tucked on the main one. Flat mode turns
    // those two small slots into real windows, EVE clamps anything under 1024x768 and they overlap;
    // preview mode is the only mode that renders them as thumbnails, but it also demotes the three
    // big slots from windows to full-resolution captures -- which is both blurrier and far more
    // expensive than simply placing the window there (see TextureReadback: a full-size tile reads
    // back at mip 0, ~13.8 MiB per frame, per tile).
    //
    // The master slot is always a real window regardless of this field.
    public string RenderMode { get; set; } = "";

    // The seat (SlotAssignment.SlotNumber) that occupies THIS position at rest in corner/grid mode.
    // null = auto-derive (legacy: position number == seat number, with leftover fallback). Set by the
    // user dragging a seat card onto a mini-map corner; ignored for the center slot (master sits there).
    public int? HomeSeat { get; set; }

    // Display-only marker for the slot table: true for whichever slot is currently the master rect.
    // NOT persisted and never read back as truth -- the master is always re-derived from geometry by
    // MainWindowViewModel.PickCenterSlot, and this is only stamped on for the UI to show it.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMasterSlot { get; set; }

    // Friendly wrapper for the slot table's combo box. RenderMode itself stays a terse, stable token
    // in settings.json; this maps it to the words shown in the UI. Not persisted -- same pattern as
    // IsMasterSlot above. An unrecognised stored value reads back as "Follow layout", which is also
    // how SlotRendersAsWindow treats it, so the UI never disagrees with the rule that runs.
    [System.Text.Json.Serialization.JsonIgnore]
    public string RenderModeDisplay
    {
        get => RenderMode switch
        {
            "Window"  => "Real window",
            "Preview" => "Live preview",
            _         => "Follow layout"
        };
        set => RenderMode = value switch
        {
            "Real window"  => "Window",
            "Live preview" => "Preview",
            _              => ""
        };
    }

    public WindowRect ToRect() => new() { X = X, Y = Y, Width = Width, Height = Height };
}
