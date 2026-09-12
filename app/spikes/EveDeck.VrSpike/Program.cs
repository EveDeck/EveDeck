// EveDeck VR spike -- THROWAWAY. Proves one thing only: that EVE client frames can be
// captured and handed to SteamVR as overlay quads laid out like the Center Master profile.
// View-only, no input, no settings, no integration with the real app.
//
// Why this shape:
//   * DWM thumbnails (what the real previews use) cannot feed a VR compositor -- they only
//     composite into an on-desktop HWND. So a VR build needs a real capture backend.
//   * The backend here is WGC, ported verbatim from branch wgc-local-previews (3fed5cc):
//     ONE shared D3D11 device, CPU readback to a GDI Bitmap, a free-VRAM floor, an fps cap,
//     and a hard fallback. Those guardrails were live-tested under the real 5x-DX12+framegen
//     load with zero faults -- WGC was dropped from the app only because DWM was faster and
//     steadier for DESKTOP previews, which is not a choice that exists in VR.
//   * PrintWindow survives as the fallback when WGC is unavailable or faults.
//   * IVROverlay.SetOverlayRaw takes a plain RGBA buffer, so there is no D3D11 interop
//     between the capture backend and the compositor.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EveDeck.Services.Wgc;
using EveDeck.VrSpike;
using Valve.VR;

internal static class Program
{
    // PW_RENDERFULLCONTENT -- without it PrintWindow returns black for GPU-rendered windows
    // like the EVE client.
    private const uint PwRenderFullContent = 0x2;

    private const int MaxTextureEdge = 1024;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetWindowTextW(nint hWnd, char[] text, int count);

    // OPSEC guard: decide "is this a real EVE client" from the LIVE window title, never from a
    // display label. In --seats mode the label is "Seat 3", so a label-based check silently fails
    // open and screenshots of real clients get written.
    private static bool IsEveWindow(nint hwnd)
    {
        try
        {
            var buffer = new char[512];
            var len = GetWindowTextW(hwnd, buffer, buffer.Length);
            if (len <= 0) return true; // unknown -> treat as sensitive
            return new string(buffer, 0, len).StartsWith("EVE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // fail closed
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private static int Main(string[] args)
    {
        var match = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "EVE";

        // Saved geometry supplies the defaults; any flag on this run overrides it. --save writes
        // the effective values back, so a good arrangement survives a restart.
        var saved = GeometryStore.Load(m => Console.WriteLine($"[cfg] {m}"));
        var worldLocked = WorldLockRequested(args);
        var allowWgc = !args.Contains("--no-wgc");
        // SetOverlayTexture is the default; --raw forces the old (leaking) SetOverlayRaw path.
        var useTexture = !args.Contains("--raw");

        // Defaults deliberately larger/closer than the first pass, which was legible-ish but soft.
        // --headlock is the default; --world pins panels to the play space instead.
        // --flat drops the master's curvature.
        var useSeats = args.Contains("--seats");

        var flat = args.Contains("--flat") || (saved.Flat && !args.Contains("--curve="));
        var masterWidth = Arg(args, "master", saved.Master);
        var previewWidth = Arg(args, "preview", saved.Preview);
        var distance = Arg(args, "dist", saved.Dist);
        var previewDrop = Arg(args, "drop", saved.Drop);
        var curve = flat ? 0f : Arg(args, "curve", saved.Curve);
        var panelOverrides = ParsePanelOverrides(args, saved.Panels);
        var geom = new Geometry(masterWidth, previewWidth, distance, previewDrop, curve, panelOverrides);

        if (args.Contains("--save"))
        {
            GeometryStore.Save(
                new VrGeometry(masterWidth, previewWidth, distance, previewDrop, curve, flat, worldLocked, panelOverrides),
                m => Console.WriteLine($"[cfg] {m}"));
        }
        const int fps = 15;

        var windows = useSeats
            ? SeatModel.Load(m => Console.WriteLine($"[seats] {m}")).Select(x => (Hwnd: x.Hwnd, Title: x.Display)).ToList()
            : FindWindows(match);
        if (windows.Count == 0)
        {
            Console.Error.WriteLine(useSeats
                ? "No previewable seats resolved from EveDeck settings.json (clients running? seats assigned?)."
                : $"No visible windows matched \"{match}\". Pass a title substring as the first argument.");
            return 1;
        }

        Console.WriteLine($"Matched {windows.Count} window(s):");
        for (var i = 0; i < windows.Count; i++) Console.WriteLine($"  [{i}] {windows[i].Hwnd:X}  {Safe(windows[i].Title)}");

        // The VRAM floor lives inside WgcCaptureDevice; this only reports why we ended up on
        // whichever backend we ended up on.
        var wgcUsable = false;
        if (allowWgc)
        {
            wgcUsable = WgcCaptureDevice.ProbeAvailable(out var detail);
            Console.WriteLine($"WGC probe: {(wgcUsable ? "available" : "unavailable")} -- {detail}");
        }
        else
        {
            Console.WriteLine("WGC disabled by --no-wgc; using PrintWindow.");
        }

        // Capture-only path: verifies the backend produces real pixels on a machine with no
        // SteamVR installed, which is otherwise impossible because OpenVR.Init gates everything.
        if (args.Contains("--vram")) { Console.WriteLine(VramLine()); WgcCaptureDevice.DisposeShared(); return 0; }

        if (args.Contains("--dry-run")) return DryRun(windows, wgcUsable, fps, args.Contains("--save-png"));

        var initError = EVRInitError.None;
        OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
        if (initError != EVRInitError.None)
        {
            Console.Error.WriteLine($"OpenVR init failed: {initError}. Is SteamVR running?");
            return 2;
        }

        var ov = OpenVR.Overlay;

        // SetOverlayRaw ships pixels over the vrserver IPC channel, which has a payload ceiling.
        // This walks sizes down until one is accepted, so the ceiling is a measured number rather
        // than a guess.
        if (args.Contains("--probe-raw")) return ProbeRaw(ov);

        var panels = new List<Panel>();

        try
        {
            panels = BuildPanels(ov, windows, geom, worldLocked, wgcUsable, fps, useTexture);

            if (panels.Count == 0 && !useSeats)
            {
                Console.Error.WriteLine("No overlays created.");
                return 3;
            }

            Console.WriteLine();
            var anchor = worldLocked ? "world-locked at the play space origin" : "head-locked";
            var source = useSeats ? "EveDeck seats" : $"window match \"{match}\"";
            var onWgc = panels.Count(p => p.Wgc is not null || p.Texture is not null);
            var mode = useTexture ? "SetOverlayTexture" : "SetOverlayRaw";
            Console.WriteLine($"{panels.Count} overlay(s) live ({onWgc} on WGC, {panels.Count - onWgc} on PrintWindow) via {mode}, {anchor}, from {source}, ~{fps}fps. Ctrl+C to quit.");
            var fov = 2 * Math.Atan(masterWidth / 2 / distance) * 180 / Math.PI;
            Console.WriteLine($"  geometry: master {masterWidth}m @ {distance}m = {fov:F0} deg wide, curve {curve}; previews {previewWidth}m dropped {previewDrop}m");
            Console.WriteLine($"  tune with --master= --preview= --dist= --drop= --curve= | --flat --headlock --world --seats --save");
            if (geom.Panels.Count > 0) Console.WriteLine($"  per-panel overrides: {string.Join(", ", geom.Panels.Select(kv => $"seat {kv.Key}"))}");

            var stop = false;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

            // Master promotion happens in the app, not here -- so watch settings.json and move the
            // centre panel when the master seat changes.
            var seatsStamp = SeatModel.LastWriteUtc();
            var seatCheck = Stopwatch.StartNew();

            const int frameDelay = 1000 / fps;
            var lastReport = Stopwatch.StartNew();
            while (!stop)
            {
                var sw = Stopwatch.StartNew();

                // OpenVR requires an application to drain its event queues. Leaving them unpolled
                // lets them overflow, after which calls start returning RequestFailed -- which is
                // exactly what this spike hit (every panel died at ~50 frames, ~3.5s in).
                DrainEvents(ov, panels);

                foreach (var panel in panels) Pump(ov, panel);

                if (useSeats && seatCheck.ElapsedMilliseconds >= 1500)
                {
                    seatCheck.Restart();
                    var stamp = SeatModel.LastWriteUtc();
                    if (stamp != seatsStamp)
                    {
                        seatsStamp = stamp;
                        var fresh = SeatModel.Load();
                        var freshMaster = fresh.FirstOrDefault(x => x.IsMaster)?.Hwnd ?? nint.Zero;
                        var currentMaster = panels.Count > 0 ? panels[0].Hwnd : nint.Zero;

                        var sameSet = fresh.Count == panels.Count &&
                                      fresh.All(f => panels.Any(pn => pn.Hwnd == f.Hwnd));

                        if (sameSet && freshMaster != nint.Zero && freshMaster != currentMaster)
                        {
                            // Same windows, different master: reorder and re-apply geometry only,
                            // keeping every capture session alive.
                            var promoted = panels.First(pn => pn.Hwnd == freshMaster);
                            panels.Remove(promoted);
                            panels.Insert(0, promoted);
                            for (var pi = 0; pi < panels.Count; pi++)
                                ApplyPanelGeometry(ov, panels[pi].Handle, pi == 0, pi, panels.Count, geom, worldLocked, panels[pi].Title);
                            Console.WriteLine($"  master moved to {Safe(promoted.Title)}");
                        }
                        else if (!sameSet)
                        {
                            // A client launched or closed. Rebuild everything: capture sessions are
                            // bound to window handles, so they cannot simply be re-pointed.
                            Console.WriteLine($"  seat set changed ({panels.Count} -> {fresh.Count}) -- rebuilding panels");
                            TearDown(ov, panels);
                            var refreshed = fresh.Select(x => (Hwnd: x.Hwnd, Title: x.Display)).ToList();
                            panels = refreshed.Count > 0
                                ? BuildPanels(ov, refreshed, geom, worldLocked, wgcUsable, fps, useTexture)
                                : [];
                            if (panels.Count == 0) Console.WriteLine("  no previewable seats right now -- waiting");
                        }
                    }
                }

                // Periodic tallies rather than a one-shot error line, so a transient startup
                // failure is distinguishable from a persistent one.
                if (lastReport.ElapsedMilliseconds >= 2000)
                {
                    lastReport.Restart();
                    foreach (var panel in panels)
                    {
                        var detail = panel.Failed > 0 ? $" last={panel.LastError} @{panel.LastSize}" : string.Empty;
                        Console.WriteLine($"  [{Safe(panel.Title)}] ok={panel.Ok} failed={panel.Failed}{detail}");
                    }
                    Console.WriteLine($"  {VramLine()}");
                    Console.WriteLine();
                }

                var remaining = frameDelay - (int)sw.ElapsedMilliseconds;
                if (remaining > 0) Thread.Sleep(remaining);
            }
        }
        finally
        {
            TearDown(ov, panels);
            WgcCaptureDevice.DisposeShared();
            OpenVR.Shutdown();
        }

        Console.WriteLine("Shut down cleanly.");
        return 0;
    }

    // The PNG written by the dry run comes from the GDI Bitmap, so it proves the capture but
    // says nothing about the RGBA buffer handed to SetOverlayRaw. Find the most colour-skewed
    // pixel in the frame and confirm the buffer really is R,G,B,A at that offset -- a swapped
    // red/blue would otherwise only show up once a headset is on.
    private static bool ChannelOrderOk(Bitmap frame, byte[] rgba, out string detail)
    {
        var bestX = 0;
        var bestY = 0;
        var bestSkew = -1;

        for (var y = 0; y < frame.Height; y += Math.Max(1, frame.Height / 64))
        {
            for (var x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 64))
            {
                var p = frame.GetPixel(x, y);
                var skew = Math.Abs(p.R - p.B);
                if (skew > bestSkew) { bestSkew = skew; bestX = x; bestY = y; }
            }
        }

        var pixel = frame.GetPixel(bestX, bestY);
        var i = ((bestY * frame.Width) + bestX) * 4;
        var ok = rgba[i] == pixel.R && rgba[i + 1] == pixel.G && rgba[i + 2] == pixel.B;

        detail = $"most colour-skewed sample ({bestX},{bestY}) R{pixel.R} G{pixel.G} B{pixel.B} skew {bestSkew}; buffer {rgba[i]},{rgba[i + 1]},{rgba[i + 2]},{rgba[i + 3]}";
        if (bestSkew == 0) detail += " -- WARNING: frame is greyscale here, red/blue swap undetectable";
        return ok;
    }

    private static int ProbeRaw(CVROverlay ov)
    {
        ulong handle = 0;
        var createErr = ov.CreateOverlay("evedeck.vrspike.probe", "EveDeck Raw Probe", ref handle);
        if (createErr != EVROverlayError.None)
        {
            Console.Error.WriteLine($"CreateOverlay failed: {createErr}");
            return 5;
        }

        ov.SetOverlayWidthInMeters(handle, 1.0f);
        ov.ShowOverlay(handle);

        int[] widths = [2560, 1920, 1440, 1280, 1024, 960, 800, 640, 512, 384, 256, 192, 128, 64];
        var largestOk = 0;

        foreach (var w in widths)
        {
            var h = Math.Max(1, w * 9 / 16);
            var buffer = new byte[w * h * 4];
            // A flat mid-grey; content is irrelevant, only the payload size is under test.
            Array.Fill(buffer, (byte)128);

            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            EVROverlayError err;
            try
            {
                err = ov.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)w, (uint)h, 4);
            }
            finally
            {
                pinned.Free();
            }

            var mib = buffer.Length / 1024.0 / 1024.0;
            Console.WriteLine($"  {w,5}x{h,-5} {buffer.Length,10} bytes ({mib,5:F2} MiB) -> {err}");
            if (err == EVROverlayError.None && largestOk == 0) largestOk = w;
        }

        ov.DestroyOverlay(handle);
        OpenVR.Shutdown();

        Console.WriteLine();
        Console.WriteLine(largestOk > 0
            ? $"Largest accepted width: {largestOk} ({largestOk * (largestOk * 9 / 16) * 4 / 1024.0 / 1024.0:F2} MiB)."
            : "No size was accepted -- the failure is not payload size.");
        return 0;
    }

    private static int DryRun(List<(nint Hwnd, string Title)> windows, bool wgcUsable, int fps, bool savePng)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "evedeck-vrspike");
        Directory.CreateDirectory(outDir);
        var failures = 0;

        for (var i = 0; i < windows.Count; i++)
        {
            var panel = new Panel(0, windows[i].Hwnd, windows[i].Title);
            if (wgcUsable) TryAttachWgc(panel, fps);

            try
            {
                if (!TargetSize(panel.Hwnd, out var width, out var height))
                {
                    Console.Error.WriteLine($"  [{i}] \"{Safe(panel.Title)}\": window not capturable (minimised or zero-sized).");
                    failures++;
                    continue;
                }

                // WGC needs a frame or two to arrive after Start(); poll rather than assume.
                Bitmap? frame = null;
                var backend = panel.Wgc is null ? "PrintWindow" : "WGC";
                for (var attempt = 0; attempt < 30 && frame is null; attempt++)
                {
                    frame = panel.Wgc?.TryGetResizedFrame(width, height);
                    if (frame is null) Thread.Sleep(50);
                }

                if (frame is null)
                {
                    frame = CaptureViaPrintWindow(panel.Hwnd, width, height);
                    if (panel.Wgc is not null && frame is not null) backend = "PrintWindow (WGC yielded no frame)";
                }

                if (frame is null)
                {
                    Console.Error.WriteLine($"  [{i}] \"{Safe(panel.Title)}\": no frame from any backend.");
                    failures++;
                    continue;
                }

                using (frame)
                {
                    var rgba = ToRgba(frame, out var w, out var h);
                    var nonBlack = rgba.Where((_, idx) => idx % 4 != 3).Any(b => b != 0);
                    var path = Path.Combine(outDir, $"panel{i}.png");
                    // Screenshots of real clients carry character AND system names. Writing them is
                    // opt-in, and still refused for anything that looks like an EVE client.
                    if (!savePng) path = "(not written -- pass --save-png)";
                    else if (IsEveWindow(panel.Hwnd)) path = "(not written -- EVE client)";
                    else frame.Save(path, ImageFormat.Png);
                    Console.WriteLine($"  [{i}] \"{Safe(panel.Title)}\": {backend}, {w}x{h}, {rgba.Length} bytes RGBA, {(nonBlack ? "has content" : "ALL BLACK")} -> {path}");
                    if (!nonBlack) failures++;
                    if (!ChannelOrderOk(frame, rgba, out var why))
                    {
                        Console.Error.WriteLine($"       channel order WRONG: {why}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"       channel order OK ({why})");
                    }
                }
            }
            finally
            {
                panel.Wgc?.Dispose();
            }
        }

        Console.WriteLine($"  {VramLine()}");
        WgcCaptureDevice.DisposeShared();
        Console.WriteLine(failures == 0 ? "Dry run OK." : $"Dry run finished with {failures} problem(s).");
        return failures == 0 ? 0 : 4;
    }

    private static void TryAttachWgc(Panel panel, int maxFps, bool useTexture = false)
    {
        if (useTexture)
        {
            try
            {
                var src = new VrTextureSource(panel.Hwnd, msg => Console.WriteLine($"[wgc {Safe(panel.Title)}] {msg}"));
                panel.Texture = src;
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WGC texture source failed for \"{Safe(panel.Title)}\", falling back: {ex}");
            }
        }

        try
        {
            var session = new WgcTileCaptureSession(panel.Hwnd, maxFps, msg => Console.WriteLine($"[wgc {Safe(panel.Title)}] {msg}"));
            if (session.Faulted)
            {
                Console.Error.WriteLine($"WGC unavailable for \"{Safe(panel.Title)}\": {session.FaultReason}. Falling back to PrintWindow.");
                session.Dispose();
                return;
            }
            panel.Wgc = session;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WGC init threw for \"{Safe(panel.Title)}\", falling back to PrintWindow: {ex}");
        }
    }

    // Free local VRAM, sampled from the capture device's adapter. The 2026-07 hard-lock was a
    // VRAM-ceiling event, so this is the number to watch when testing under real EVE load.
    // OPSEC: a logged-in EVE client's title is "EVE - <character name>". Character names must never
    // reach console output, logs, or anything committed, so collapse every EVE title to a seat
    // label. Non-EVE windows (used for desktop testing) keep their titles.
    // Legibility is governed by angular pixel density, not capture resolution: the headset resolves
    // roughly 20 px per degree, so a panel has to span enough of your view for EVE's native text to
    // survive. These knobs exist so the sweet spot can be found in-headset without a rebuild.
    private static List<Panel> BuildPanels(CVROverlay ov, List<(nint Hwnd, string Title)> windows,
        Geometry geom, bool worldLocked, bool wgcUsable, int fps, bool useTexture)
    {
        var panels = new List<Panel>();
        for (var i = 0; i < windows.Count; i++)
        {
            ulong handle = 0;
            // Overlay keys must be unique per live overlay; a rebuild reuses indices only after the
            // previous overlays are destroyed.
            var err = ov.CreateOverlay($"evedeck.vrspike.{i}", $"EveDeck Spike {i}", ref handle);
            if (err != EVROverlayError.None)
            {
                Console.Error.WriteLine($"CreateOverlay failed for index {i}: {err}");
                continue;
            }

            ov.SetOverlayAlpha(handle, 1.0f);
            ApplyPanelGeometry(ov, handle, i == 0, i, windows.Count, geom, worldLocked, windows[i].Title);
            ov.ShowOverlay(handle);

            var panel = new Panel(handle, windows[i].Hwnd, windows[i].Title);
            if (wgcUsable) TryAttachWgc(panel, fps, useTexture);
            panels.Add(panel);
        }
        return panels;
    }

    private static void TearDown(CVROverlay ov, List<Panel> panels)
    {
        foreach (var panel in panels)
        {
            panel.Wgc?.Dispose();
            panel.Texture?.Dispose();
            ov.DestroyOverlay(panel.Handle);
        }
        panels.Clear();
    }

    // --panel=<slot>:x,y,z[,width] -- e.g. --panel=3:-1.4,0.1,-1.6,1.1 parks seat 3 on the left.
    // Merged over anything already saved, so one panel can be nudged without retyping the rest.
    private static Dictionary<string, PanelPlacement> ParsePanelOverrides(string[] args, Dictionary<string, PanelPlacement>? saved)
    {
        var result = saved is null
            ? new Dictionary<string, PanelPlacement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PanelPlacement>(saved, StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args.Where(a => a.StartsWith("--panel=", StringComparison.OrdinalIgnoreCase)))
        {
            var body = arg["--panel=".Length..];
            var split = body.Split(':', 2);
            if (split.Length != 2) { Console.Error.WriteLine($"ignoring malformed {arg} (want --panel=<slot>:x,y,z[,w])"); continue; }

            var parts = split[1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) { Console.Error.WriteLine($"ignoring malformed {arg} (need at least x,y,z)"); continue; }

            float? Num(int i) =>
                i < parts.Length && float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

            result[split[0].Trim()] = new PanelPlacement(Num(0), Num(1), Num(2), Num(3));
        }

        return result;
    }

    // Panels are labelled "Seat N" in --seats mode; overrides are keyed by that bare N.
    private static string PlacementKey(string title)
    {
        var digits = new string(title.Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : title;
    }

    // --headlock always wins over a saved world-lock, so there is a way back without editing the
    // config file.
    private static bool WorldLockRequested(string[] args) =>
        (args.Contains("--world") || GeometryStore.Load().World) && !args.Contains("--headlock");

    private sealed record Geometry(float Master, float Preview, float Distance, float Drop, float Curve,
        Dictionary<string, PanelPlacement> Panels);

    private static void ApplyPanelGeometry(CVROverlay ov, ulong handle, bool isMaster, int index, int total, Geometry g, bool worldLocked, string title = "")
    {
        g.Panels.TryGetValue(PlacementKey(title), out var over);

        var width = over?.Width ?? (isMaster ? g.Master : g.Preview);
        ov.SetOverlayWidthInMeters(handle, width);

        // A wide flat quad reads badly at the edges; a gentle curve keeps the master roughly
        // equidistant from the eye. Previews are small enough to stay flat.
        ov.SetOverlayCurvature(handle, isMaster ? g.Curve : 0f);

        var slot = SlotFor(index, total, g.Distance, g.Preview, g.Drop);
        // Each axis falls back independently, so --panel=3:,,-1.2 style partial moves still work.
        var pos = (over?.X ?? slot.X, over?.Y ?? slot.Y, over?.Z ?? slot.Z);
        SetTransform(ov, handle, pos, worldLocked);
    }

    private static float Arg(string[] args, string name, float fallback)
    {
        var prefix = $"--{name}=";
        var hit = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return fallback;
        return float.TryParse(hit[prefix.Length..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static string Safe(string title)
    {
        return title.StartsWith("EVE", StringComparison.OrdinalIgnoreCase) ? "EVE" : title;
    }

    private static string VramLine()
    {
        try
        {
            if (Direct3DInterop.TryQueryLocalVideoMemory(WgcCaptureDevice.Shared.D3DDevice, out var budget, out var usage) && budget > 0)
            {
                var freeMiB = (budget - usage) / (1024 * 1024);
                var budgetMiB = budget / (1024 * 1024);
                var usageMiB = usage / (1024 * 1024);
                return $"VRAM free {freeMiB} MiB / budget {budgetMiB} MiB (in use {usageMiB} MiB)";
            }
        }
        catch (Exception ex)
        {
            return $"VRAM n/a ({ex.GetType().Name})";
        }
        return "VRAM n/a";
    }

    private static void DrainEvents(CVROverlay ov, List<Panel> panels)
    {
        var ev = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();

        // System queue (this process's own), then each overlay's queue.
        var system = OpenVR.System;
        if (system is not null)
        {
            while (system.PollNextEvent(ref ev, size)) { }
        }

        foreach (var panel in panels)
        {
            while (ov.PollNextOverlayEvent(panel.Handle, ref ev, size)) { }
        }
    }

    private static void Pump(CVROverlay ov, Panel panel)
    {
        // Preferred path: hand the compositor the GPU texture directly. No readback, no swizzle,
        // and none of SetOverlayRaw's per-call resource leak.
        if (panel.Texture is { Faulted: false } src && src.TryGetTexture(out var texHandle, out var tw, out var th))
        {
            var texture = new Texture_t
            {
                handle = texHandle,
                eType = ETextureType.DirectX,
                eColorSpace = EColorSpace.Auto,
            };

            var texErr = ov.SetOverlayTexture(panel.Handle, ref texture);
            if (texErr == EVROverlayError.None)
            {
                panel.Ok++;
            }
            else
            {
                panel.Failed++;
                panel.LastError = texErr;
                panel.LastSize = $"{tw}x{th} tex";
            }
            return;
        }

        if (!TargetSize(panel.Hwnd, out var width, out var height)) return;

        Bitmap? frame = null;
        try
        {
            // WGC first; any fault demotes this panel to PrintWindow permanently, matching the
            // shipping branch's DemoteFaultedWgcTiles behaviour.
            if (panel.Wgc is { } wgc)
            {
                try
                {
                    frame = wgc.TryGetResizedFrame(width, height);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"WGC frame error for \"{Safe(panel.Title)}\", demoting to PrintWindow: {ex}");
                }

                var faulted = panel.Wgc is WgcTileCaptureSession { Faulted: true };
                if (frame is null && faulted)
                {
                    Console.Error.WriteLine($"WGC faulted for \"{Safe(panel.Title)}\", demoting to PrintWindow.");
                    panel.Wgc.Dispose();
                    panel.Wgc = null;
                }
            }

            frame ??= CaptureViaPrintWindow(panel.Hwnd, width, height);
            if (frame is null) return;

            var buffer = ToRgba(frame, out var w, out var h);
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var err = ov.SetOverlayRaw(panel.Handle, pinned.AddrOfPinnedObject(), (uint)w, (uint)h, 4);
                if (err == EVROverlayError.None)
                {
                    panel.Ok++;
                }
                else
                {
                    panel.Failed++;
                    panel.LastError = err;
                    panel.LastSize = $"{w}x{h}";
                }
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    // Target texture size, derived from the live window rect and capped so a 4K client does not
    // push a 33MB buffer per overlay per frame.
    private static bool TargetSize(nint hwnd, out int width, out int height)
    {
        width = height = 0;
        if (IsIconic(hwnd)) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        var srcWidth = rect.Right - rect.Left;
        var srcHeight = rect.Bottom - rect.Top;
        if (srcWidth <= 0 || srcHeight <= 0) return false;

        var scale = Math.Min(1.0, (double)MaxTextureEdge / Math.Max(srcWidth, srcHeight));
        width = Math.Max(1, (int)(srcWidth * scale));
        height = Math.Max(1, (int)(srcHeight * scale));
        return true;
    }

    private static Bitmap? CaptureViaPrintWindow(nint hwnd, int dstWidth, int dstHeight)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var srcWidth = rect.Right - rect.Left;
        var srcHeight = rect.Bottom - rect.Top;
        if (srcWidth <= 0 || srcHeight <= 0) return null;

        using var shot = new Bitmap(srcWidth, srcHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PwRenderFullContent)) return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var scaled = new Bitmap(dstWidth, dstHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(shot, 0, 0, dstWidth, dstHeight);
        }
        return scaled;
    }

    // Both backends hand back a 32bpp GDI bitmap, which is BGRA in memory; OpenVR wants RGBA.
    private static byte[] ToRgba(Bitmap bitmap, out int width, out int height)
    {
        width = bitmap.Width;
        height = bitmap.Height;

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buffer = new byte[width * height * 4];
        try
        {
            unsafe
            {
                var src = (byte*)data.Scan0;
                fixed (byte* dst = buffer)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var row = src + (y * data.Stride);
                        var outRow = dst + (y * width * 4);
                        for (var x = 0; x < width; x++)
                        {
                            var i = x * 4;
                            outRow[i + 0] = row[i + 2]; // R <- B
                            outRow[i + 1] = row[i + 1]; // G
                            outRow[i + 2] = row[i + 0]; // B <- R
                            outRow[i + 3] = 255;        // capture alpha is unreliable either way
                        }
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return buffer;
    }

    // Center Master, reprojected: master dead ahead, previews in a strip beneath it.
    private static (float X, float Y, float Z) SlotFor(int index, int total, float distance, float previewWidth, float drop)
    {
        if (index == 0) return (0f, 0f, -distance);

        var previews = total - 1;
        var slot = index - 1;
        // Space previews by their own width plus a small gutter so they never overlap.
        var spacing = previewWidth * 1.12f;
        var x = (slot - ((previews - 1) / 2.0f)) * spacing;
        return (x, -drop, -(distance * 0.97f));
    }

    private static void SetTransform(CVROverlay ov, ulong handle, (float X, float Y, float Z) pos, bool worldLocked)
    {
        var m = new HmdMatrix34_t
        {
            m0 = 1, m1 = 0, m2 = 0, m3 = pos.X,
            m4 = 0, m5 = 1, m6 = 0, m7 = pos.Y,
            m8 = 0, m9 = 0, m10 = 1, m11 = pos.Z,
        };

        if (worldLocked)
        {
            // Standing universe origin sits on the floor, so lift the rig to eye height.
            m.m7 += 1.6f;
            ov.SetOverlayTransformAbsolute(handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        }
        else
        {
            // Device index 0 is always the HMD -- panels ride the head so they cannot be lost.
            ov.SetOverlayTransformTrackedDeviceRelative(handle, 0, ref m);
        }
    }

    // Accepts a comma-separated list so a spike run can pull several distinct windows into the
    // layout ("EVE" alone is the normal case; "a,b,c" is for testing the slots).
    private static List<(nint Hwnd, string Title)> FindWindows(string match)
    {
        var terms = match.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) terms = [match];

        var results = new List<(nint, string)>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == nint.Zero) continue;
                var title = proc.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (terms.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                   proc.ProcessName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add((proc.MainWindowHandle, title));
                }
            }
            catch
            {
                // processes we cannot query are not candidates
            }
            finally
            {
                proc.Dispose();
            }
        }
        return results;
    }

    private sealed class Panel(ulong handle, nint hwnd, string title)
    {
        public ulong Handle { get; } = handle;
        public nint Hwnd { get; } = hwnd;
        public string Title { get; } = title;
        public ITileCaptureSession? Wgc { get; set; }
        public VrTextureSource? Texture { get; set; }
        public int Ok { get; set; }
        public int Failed { get; set; }
        public EVROverlayError LastError { get; set; }
        public string? LastSize { get; set; }
    }
}
