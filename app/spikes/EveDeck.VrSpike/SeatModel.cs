using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EveDeck.VrSpike;

// Reads EveDeck's live settings.json so VR panels follow the real seat layout instead of a
// window-title guess. Read-only: the app owns this file and rewrites it on every save, so the
// spike must never write to it.
//
// OPSEC: Assignments[].Label and EsiCharacters[].CharacterName hold real character names. Nothing
// here reads or surfaces them -- seats are identified by SlotNumber alone.
internal static class SeatModel
{
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    internal sealed record Seat(int SlotNumber, bool IsMaster, nint Hwnd)
    {
        // Display name never derived from config text -- slot number only.
        public string Display => $"Seat {SlotNumber}";
    }

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveDeck", "settings.json");

    public static DateTime LastWriteUtc()
    {
        try { return File.GetLastWriteTimeUtc(SettingsPath); }
        catch { return DateTime.MinValue; }
    }

    /// Resolved, previewable seats ordered master-first then by slot. Empty if settings are
    /// missing or no seat currently resolves to a live window.
    public static List<Seat> Load(Action<string>? log = null)
    {
        var results = new List<Seat>();

        string json;
        try
        {
            // The app may be mid-write; read a snapshot rather than locking it.
            using var fs = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            json = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            log?.Invoke($"seats: cannot read settings.json ({ex.GetType().Name})");
            return results;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            log?.Invoke($"seats: settings.json did not parse ({ex.GetType().Name})");
            return results;
        }

        if (!root.TryGetProperty("Assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
        {
            log?.Invoke("seats: no Assignments array");
            return results;
        }

        var masterSlot = IntOr(root, "MasterSlotNumber", -1);
        var live = LiveWindowsByTitle();
        var claimed = new HashSet<nint>();
        var anyExplicitMaster = false;

        foreach (var a in assignments.EnumerateArray())
        {
            var slot = IntOr(a, "SlotNumber", -1);
            if (slot < 0) continue;

            // PreventPreview seats are deliberately not captured anywhere in EveDeck; honour that.
            if (Flag(a, "PreventPreview")) continue;

            var hwnd = ResolveWindow(a, live);
            if (hwnd == nint.Zero) continue;
            if (!claimed.Add(hwnd)) continue; // two seats must never drive one window

            var explicitMaster = Flag(a, "IsMaster");
            if (explicitMaster) anyExplicitMaster = true;
            results.Add(new Seat(slot, explicitMaster, hwnd));
        }

        // The two master signals can disagree (observed live: MasterSlotNumber=1 while slot 5 had
        // IsMaster=true). The per-assignment flag is the specific one, so it wins; MasterSlotNumber
        // is only a fallback when no assignment claims master.
        if (!anyExplicitMaster && masterSlot >= 0)
        {
            for (var i = 0; i < results.Count; i++)
                if (results[i].SlotNumber == masterSlot)
                    results[i] = results[i] with { IsMaster = true };
        }

        // Master first so it takes the centre slot; the rest keep the layout's own order.
        results.Sort((x, y) =>
            x.IsMaster != y.IsMaster ? (x.IsMaster ? -1 : 1) : x.SlotNumber.CompareTo(y.SlotNumber));

        return results;
    }

    // settings.json stores only LAST-KNOWN pids/handles, which go stale the moment clients are
    // relaunched -- EveDeck itself rebinds seats live by window title. So do the same: read titles
    // to match, never to display.
    private static Dictionary<string, nint> LiveWindowsByTitle()
    {
        var map = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == nint.Zero) continue;
                var title = proc.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                map.TryAdd(title, proc.MainWindowHandle);
            }
            catch { /* not a candidate */ }
            finally { proc.Dispose(); }
        }
        return map;
    }

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // These fields are routinely null in settings.json (an unassigned seat, a client that has not
    // launched yet). TryGetInt32 throws on a null element rather than returning false, so the
    // ValueKind check has to come first.
    private static int IntOr(JsonElement e, string name, int fallback)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return fallback;
        return v.TryGetInt32(out var value) ? value : fallback;
    }

    // Prefer the recorded handle (cheapest and exact), then fall back to the recorded PID. Titles
    // are never used to match -- they carry character names and go stale on every login.
    private static nint ResolveWindow(JsonElement assignment, Dictionary<string, nint> live)
    {
        if (!assignment.TryGetProperty("AssignedWindows", out var windows) || windows.ValueKind != JsonValueKind.Array)
            return nint.Zero;

        foreach (var w in windows.EnumerateArray())
        {
            var pid = IntOr(w, "LastProcessId", 0);

            if (w.TryGetProperty("LastHandleHex", out var h) && h.ValueKind == JsonValueKind.String)
            {
                var raw = h.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var text = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
                    if (long.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var handleValue))
                    {
                        var hwnd = (nint)handleValue;
                        // A recycled handle can point at an unrelated window, so confirm the PID.
                        if (IsWindow(hwnd) && (pid == 0 || OwnedBy(hwnd, pid))) return hwnd;
                    }
                }
            }

            if (pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (proc.MainWindowHandle != nint.Zero && IsWindow(proc.MainWindowHandle))
                        return proc.MainWindowHandle;
                }
                catch
                {
                    // process gone -- try the next recorded window
                }
            }
        }

        // Stale ids are the normal case after a relaunch; fall back to the recorded titles.
        foreach (var w in windows.EnumerateArray())
        {
            if (!w.TryGetProperty("Title", out var t) || t.ValueKind != JsonValueKind.String) continue;
            var title = t.GetString();
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (live.TryGetValue(title, out var hwnd) && IsWindow(hwnd)) return hwnd;
        }

        return nint.Zero;
    }

    private static bool OwnedBy(nint hwnd, int pid)
    {
        GetWindowThreadProcessId(hwnd, out var actual);
        return actual == (uint)pid;
    }
}
