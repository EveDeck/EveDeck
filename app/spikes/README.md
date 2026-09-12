# spikes/ — throwaway proofs of concept

Nothing in this directory is shipping code. These projects are **deliberately excluded from
`EveDeck.sln`**, so `dotnet build`, `dotnet test`, and `dotnet publish` on the app never see
them and their dependencies (OVRSharp's native `openvr_api.dll`, Vortice) never reach
`app\publish`.

## EveDeck.VrSpike

Puts EveDeck's Center Master layout into SteamVR: one large centre panel for the master seat,
the rest as a strip of previews beneath it. View-only — no input, no settings UI.

```powershell
cd app\spikes\EveDeck.VrSpike

# Drive panels from EveDeck's real seat layout (settings.json)
dotnet run -- --seats

# Flat panels, pinned to the play space instead of your head
dotnet run -- --seats --flat --world

# Tune geometry live, and persist it
dotnet run -- --seats --master=3.6 --preview=0.9 --dist=1.5 --drop=1.2 --save

# Park seat 3 off to the left, slightly larger, leaving the others on the strip
dotnet run -- --seats --panel=3:-1.6,0.3,-1.9,1.1 --save

# Capture only, no VR (works with SteamVR absent) + VRAM report
dotnet run -- --seats --dry-run
dotnet run -- --vram
```

`--seats` sources panels from EveDeck. Without it, the first positional argument is a
comma-separated window-title/process match (`dotnet run -- "Discord,Brave"`), which is how the
spike is tested without EVE running.

| flag | effect |
|---|---|
| `--seats` | panels follow `Assignments[]` in EveDeck's settings.json |
| `--headlock` / `--world` | panels ride your view (default) or pin to the play space |
| `--flat` | no curvature on the master panel |
| `--master= --preview= --dist= --drop= --curve=` | shared geometry, in metres |
| `--panel=<slot>:x,y,z[,w]` | park one seat independently; omitted axes fall back |
| `--save` | persist the effective geometry to `%LOCALAPPDATA%\EveDeckr-spike.json` |
| `--raw` | force the old `SetOverlayRaw` path (see below — it breaks) |
| `--no-wgc` | force the PrintWindow fallback |
| `--dry-run` / `--vram` / `--probe-raw` | diagnostics, no VR needed |

### Why it is built this way

**DWM thumbnails cannot feed a VR compositor.** `DwmRegisterThumbnail` only composites into an
on-desktop HWND — there is no texture or buffer to hand to anyone. VR therefore needs a capture
backend the shipping app does not have.

**Capture is WGC**, ported verbatim from branch `wgc-local-previews` (`3fed5cc`) and compiled
unmodified. It carries every guardrail added after the 2026-07 hard-lock: one *shared* D3D11
device, CPU-free GPU path, a 512 MiB free-VRAM floor, an fps cap, and per-panel fallback. Measured
under 5 live DX12 clients: **our VRAM stays flat at ~96-186 MiB** while the OS-granted budget
contracts from 15.4 GB to ~3-4 GB under EVE + compositor pressure. Footprint does not scale with
client count — that is the whole point of the shared device.

**Upload is `SetOverlayTexture`, never `SetOverlayRaw`.** Raw uploads leak a resource per call:
every panel accepted a fixed number of frames and then returned `RequestFailed` forever — ~50,
or exactly 112 once event queues were drained. Payload size is *not* the cause (`--probe-raw`
accepts sizes up to 14 MiB). Handing over the GPU texture also removes the CPU readback and the
BGRA→RGBA swizzle, so it is faster as well as correct. `--raw` reproduces the failure.

**OpenVR apps must drain their event queues** (`PollNextEvent` + `PollNextOverlayEvent`) or calls
begin failing. Necessary, but on its own it only moved the raw-path ceiling from 50 to 112.

### Legibility

Text sharpness is governed by **angular pixel density**, not capture resolution — full-resolution
textures reach the compositor untouched. A headset resolves roughly 20 px per degree, so a panel
must span enough of your view for EVE's native text to survive. In rough order of impact:

1. EVE's in-game UI scale (90% → 100% was a clear improvement)
2. streaming bitrate/quality — text is the worst case for video compression
3. panel geometry (`--master`, `--dist`)

Some softness is unavoidable: a 2560-wide client across 83° cannot be pixel-perfect. Aim for
comfortably readable.

### OPSEC

EVE window titles are `EVE - <character name>`, and client pixels show character *and* system
names. This is a hard rule in CLAUDE.md, and the spike has already violated it twice during
development, so the guards are deliberate:

- seats are identified by **slot number only**; `Assignments[].Label` and `EsiCharacters[]` are
  never read
- every console path runs titles through `Safe()`
- `WindowCaptureSource` logs frame size, never `_item.DisplayName` (**the same line still logs the
  raw title on branch `wgc-local-previews`** — a latent leak into EveDeck's own log if WGC is ever
  revived there)
- `--dry-run` writes no screenshots unless `--save-png` is passed, and still refuses for any window
  whose **live** title starts with `EVE`. The check reads the real title via `GetWindowTextW` and
  fails closed — an earlier version tested a display label, which in `--seats` mode is `"Seat 3"`
  and silently failed open.

Any VR or desktop capture of this running belongs in a scratch directory, never the repo or site.

### Known gaps

- Seat→window binding falls back to matching recorded titles, because `settings.json` stores only
  last-known PIDs/handles and they go stale on every relaunch.
- `MasterSlotNumber` and `Assignments[].IsMaster` can disagree in live config (observed: 1 vs 5).
  The per-assignment flag wins here; `MasterSlotNumber` is a fallback.
- Per-panel placement (`--panel=`) is parse- and persistence-verified but **not yet confirmed in a
  headset** — SteamVR was down when it was added.
- The six `Services/Wgc/` files are now duplicated between this spike and the app (merged to main in
  `55ba214`). OPSEC or capture fixes must be applied in **both** places until the spike either
  references the app's copy or is retired.
