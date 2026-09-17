# EveDeck Intel

Mirrors EVE Online intel channels from a PC's chat logs onto an Android tablet over the LAN.

A JVM daemon on the gaming PC tails the EVE client's chat logs, parses intel lines into typed
tokens, and serves them over a WebSocket. The tablet app shows a live feed, jump distance from a
chosen character, a region map with sovereignty and kill activity, and raises notifications for
hostiles nearby.

Nothing leaves the local network. No account, no SSO, no telemetry. ESI is read-only and
unauthenticated — everything used is public data.

---

## Why this exists, and why it is not a port

The starting question was whether [RIFT Intel Fusion Tool](https://gitlab.com/rift-intel-fusion-tool/rift-intel-fusion-tool)
could be repackaged as an APK. It cannot:

- RIFT is ~112k lines of Kotlin built on **Compose Desktop + AWT** — `JFileChooser`, `SystemTray`,
  `java.awt.peer.WindowPeer`, JNA, JOAL, JDBC/SQLite, 30+ OS windows. None of that exists on Android.
- More fundamentally, RIFT's premise is tailing EVE's log files **on the same machine**, and there is
  no EVE client on Android. A perfect port would launch to a permanently empty feed.

So this is independent work. The log format was determined by inspecting real log files; universe
data comes from CCP's published SDE. **No RIFT code was copied.** RIFT's repository has no LICENSE
file, which means all rights reserved — do not lift code from it, and do not bundle its `static.db`.

---

## Layout

| Module | Target | Contents |
|---|---|---|
| `shared/` | KMP → JVM + Android AAR | Log format, intel parser, universe graph, wire protocol |
| `daemon/` | JVM | Log tailer, dedup, location tracking, Ktor server, ESI, image proxy |
| `android/` | APK | Feed, map, settings, foreground service, alerts |
| `tools/` | Python | SDE download + `universe.json` conversion |

`shared` deliberately has **no platform dependencies** — no SQLite, no AWT, no Android APIs. It is
pure Kotlin plus kotlinx-serialization and coroutines, so the same parser runs on both sides and is
testable without a device.

---

## Building and running

```bash
export JAVA_HOME="C:/Program Files/Android/Android Studio/jbr"   # JBR 25

./gradlew :daemon:installDist          # build the PC daemon
./daemon/build/install/daemon/bin/daemon.bat

./gradlew :android:assembleDebug       # build the APK
adb install -r android/build/outputs/apk/debug/android-debug.apk
```

The daemon prints the address to enter on the tablet:

```
Listening on :31337
  tablet URL:  ws://192.168.1.225:31337/intel
```

### Tuning the parser against real logs

```bash
./daemon/build/install/daemon/bin/daemon.bat --validate --limit 60
```

Replays recent history and prints what it understood plus everything it failed to classify. **This
is the intended workflow for improving `Vocabulary.kt`** — look at what lands under
`NOT CLASSIFIED AS INTEL` and add the surface forms that alliance actually uses. Current rate is
~84% of unique lines classified as intel on real `alliance.intel` traffic.

### Configuration

`eveintel.properties`, read from the working directory:

- `chatlogs.dir` — auto-detects `Documents/EVE/logs/Chatlogs` and the OneDrive-redirected variant.
- `intel.channels` — comma separated. **Normally you don't edit this**: the tablet's Settings tab
  writes it. `Local` is always tailed regardless, because it is how location is tracked.
- `scope.regions` — the regions the channel covers, taken from the channel MOTD. This is what makes
  `9UY` resolve unambiguously to `9UY4-H`.
- `logs.startFromEnd` — `true` for normal operation; `false` replays each active file from the top.

---

## The EVE chat log format

Verified against real files, not documentation:

- Path `Documents/EVE/logs/Chatlogs`, name `<Channel>_<yyyyMMdd>_<HHmmss>_<characterId>.txt`
- **UTF-16LE** with a BOM at the start of the file
- A further **UTF-8 BOM (`\uFEFF`) prefixed to every message line**
- CRLF line endings
- Header block of `Key: value` pairs, terminated by a 63-dash rule
- Messages: `[ 2026.09.17 17:32:58 ] Character Name > text`

Three things follow from this that are easy to get wrong:

**Polling, not a watcher.** EVE holds its log files open, and Windows does not refresh directory
metadata for open handles — `WatchService` and `Files.size()` both go stale. `LogTailer` opens a
`FileChannel` and asks *it* for the size, tracking a byte offset per file. Do not "optimise" this
into a filesystem watcher.

**Offsets must stay even.** UTF-16 is two bytes per unit; landing mid-character corrupts the next
read. See `alignToEven`.

**Location comes from Local.** The `Local` channel logs
`EVE System > Channel changed to Local : 8DL-CP` on every jump. That is where jump distances are
measured from — no ESI, no SSO, no API key. This is the single most useful trick in the codebase.

---

## Intel parsing

The key observation from real channel traffic is that **two or more spaces separate entities**:

```
5-9L3H*  Halvard  Petra Vance  KrenV  Exequror Navy Issue* 3  Exequror* 1
```

So `IntelParser` works segment by segment, trying to match each segment as a whole before scanning
inside it. That one convention removes most of the ambiguity that would otherwise need a character
name lookup.

Token types: `System`, `Player`, `Ship` (with count), `Count` (`+3` / `4=` / `x2`), `Kw` (keyword),
`Question` (`location?`, `ship?`), `Url`, `Word`.

### Two rules worth preserving

**System abbreviations only resolve for tokens containing a digit or a hyphen.** Every nullsec name
does; ordinary English words do not. Without this guard, prefix matching turns chatter into system
reports. Exact names still match anywhere in the universe.

**A message needs a named system to count as intel.** `Nora DarkStar its my anathema` mentions a
ship but is conversation; `1GH-48 clr` names a system and is not. The exception is a short
keyword-or-question-only line with no unmatched words, which is a follow-up to the line before. See
`isIntel()`. Loosening this visibly increases false positives — it was 91% "intel" before the rule
and 84% after, and the 84% is correct.

### Deduplication

With five characters logged in, every channel line lands in five log files. `IntelPipeline` keys on
an FNV-1a hash of timestamp + author + text so the tablet sees each line once.

---

## Universe data

`tools/build_universe.py` turns Fuzzwork's SDE CSV dumps into `shared/src/commonMain/resources/universe.json`:
5,485 k-space systems, 6,989 stargate pairs, 423 ship types, ~1 MB.

Held in memory as a graph on **both** sides, which is why the wire carries only system ids — never
names, coordinates or topology. Jump distance is a BFS from the followed character's system, cheap
enough to recompute on every jump rather than cache.

Notes for whoever regenerates it:

- CSVs are at `https://www.fuzzwork.co.uk/dump/latest/csv/<Table>.csv` — **plain, uncompressed**. The
  commonly-cited `.csv.bz2` path 404s and returns an HTML error page that lands silently in your
  output file. Verify the first line is a CSV header.
- The jumps table stores each connection twice; dedupe to `[min, max]` pairs.
- `regionID >= 11000000` is wormhole/abyssal space and is filtered out.
- Jita has **7** rows in `mapSolarSystemJumps`, not the 4 physical stargates people expect. The SDE
  is the authority; this is not a conversion bug.

The file is currently packaged **twice** in the APK (`assets/universe.json` and again at the root via
the AAR's Java resources), wasting ~1 MB. Worth fixing.

---

## Wire protocol

`shared/wire/Protocol.kt`, JSON over WebSocket at `/intel`, `type` as the discriminator.

Server → tablet: `Snapshot` (sent on connect so a tablet joining mid-fight has history), `Intel`,
`Location`, `Channels`, `Characters`, `Sovereignty`, `Stats`, `Heartbeat`.
Tablet → server: `Hello`, `Follow`, `SetChannels`.

**No auth, no TLS.** Deliberate for a LAN service carrying data already public to everyone in the
channel. **Do not port-forward it.** For access from outside the house, put both devices on a
Tailscale tailnet rather than opening a port. If this ever needs to leave the LAN, auth has to be
designed in — not bolted on.

---

## ESI integration

All ESI traffic happens on the **daemon**, never the tablet. Endpoints used (all public):

| Endpoint | Purpose | Cadence |
|---|---|---|
| `POST /universe/ids/` | character name → id | batched, on demand |
| `POST /characters/affiliation/` | id → corp + alliance | batched, on demand |
| `POST /universe/names/` | corp/alliance names | batched |
| `GET /alliances/{id}/` | ticker | once per holder |
| `GET /sovereignty/map/` | sov holder per system | hourly |
| `GET /universe/system_kills/`, `/system_jumps/` | activity heat | hourly |

The hourly cadence matches CCP's own cache headers — polling faster returns identical bytes.

`CharacterResolver` caches permanently to `cache/characters.json`, **including names that failed to
resolve**. Intel channels are full of typos and nicknames; retrying them forever is pointless
traffic. There is also a cheap pre-filter (3–37 chars, letters/digits/space/apostrophe/hyphen) so
parser misreads never reach the API.

### Image proxy

The tablet never contacts `images.evetech.net`. It asks the daemon for
`/img/<category>/<id>/<variant>?size=<n>`; the daemon fetches once, caches to `cache/images/`, and
serves from disk. This keeps the app LAN-only and works even if the tablet has no internet.
Categories and sizes are allow-listed — it is not an open proxy.

ESI asks third-party tools to identify themselves via User-Agent so CCP can make contact if a tool
misbehaves. `EsiClient` sets one. **Keep it, and put a real contact in it before any wider release.**

---

## Android specifics

**Version pins are deliberate, not laziness.** `platforms;android-37` does not exist in the SDK
repository yet, and current AndroidX refuses to build against anything lower. Held back:
Compose UI **1.11.4**, core-ktx **1.17.0**, activity-compose **1.12.4**, lifecycle **2.9.4**,
Coil **3.4.0**. Coil 3.5+ needs 37 itself *and* drags Compose 1.12 in transitively, so
`android/build.gradle.kts` carries a `resolutionStrategy` rule holding the whole `androidx.compose.*`
group (except material/material3). **Revisit all of it when android-37 ships.**

Other toolchain facts:

- **AGP 9 dropped `com.android.library` + Kotlin Multiplatform.** `shared` uses
  `com.android.kotlin.multiplatform.library` with the `androidLibrary {}` DSL (already deprecated in
  favour of `android {}`; migrate when convenient).
- **AGP 9 has Kotlin built in** — applying `org.jetbrains.kotlin.android` is a hard error.
- AGP 9.2.1 requires Gradle 9.4.1+.
- Install SDK packages with `cmdline-tools/latest/bin/android.exe sdk install "platforms/android-36"`
  (slash form). The old `sdkmanager.bat --install "platforms;android-36"` reports "Package not found".

**`ws://` is cleartext and Android blocks it by default since Android 9.** Handled by
`network_security_config.xml`. Note that Android's network security config matches domains and
literal hosts only — **no CIDR** — so "permit private ranges" cannot be expressed and it has to be
global. The app talks to nothing but the configured daemon, so the practical exposure is the same.

The WebSocket client's reconnect loop catches broadly (daemon down, wifi asleep, PC rebooting are all
the same response) but **logs the reason**. It previously swallowed exceptions silently, which made a
permanently-failing cause indistinguishable from a daemon that simply wasn't running, and cost real
debugging time. Keep the logging.

---

## Relationship to EveDeck

This lives at `intel/` inside the EveDeck repository, alongside `app/` (the C# WPF desktop app).
They share a repo, a licence and a brand — they do **not** share a runtime. EveDeck is C# / .NET 10;
this is Kotlin/JVM + Android. "Integration" therefore means a sibling component, never merged code.

The shipping decision is **a standalone PC daemon plus the APK**. Someone who wants intel on a
tablet and has no interest in EveDeck's window management downloads those two things and nothing
else. The daemon stays Kotlin and is packaged as a self-contained Windows executable with a bundled
runtime, so there is no "install Java" step — the same lesson `app/publish` learned about the .NET
runtime prompt.

EveDeck itself may later host the same WebSocket endpoint natively, letting existing EveDeck users
skip the second process. That is an addition, not a replacement: the standalone daemon remains the
product for everyone else. If that C# port happens, port the tests in `daemon/src/test/` first —
the parser is the part with the hard-won behaviour, and those cases are its specification.

**EveDeck bundles no universe data.** It resolves ID→name from ESI at runtime and has no stargate
graph, so anything needing jump distance or routing has to bring `universe.json` or an equivalent.


## Known gaps

- **Not soak-tested.** It has run for minutes, not days. Watch for offset drift on log rotation and
  memory growth in the 500-message ring buffer.
- **217 k-space systems have zero stargate entries** in the SDE. Unexplained. They will show as
  unreachable for jump distance.
- `universe.json` is packaged twice in the APK (~1 MB wasted).
- Ship name aliases in `Vocabulary.kt` are a hand-written starter set from one alliance's habits.
  Expect to grow it — that is the intended maintenance path, via `--validate`.
- Character name detection is heuristic (capitalised runs inside a segment). It has no corpus to
  validate against before ESI resolution, so unresolvable names are normal and expected.
- The map projects EVE's 3D coordinates on the x/z plane. Fine for reading a region; it is not
  Dotlan's hand-tuned layout.
- Alliance ticker lookups are one call each and run sequentially. Fine at 99 sov holders; would need
  batching if it ever covered more.
