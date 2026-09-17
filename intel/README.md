# EVE Intel — tablet intel reader

Mirrors your EVE Online alliance intel channels onto an Android tablet over your LAN.

A PC daemon tails the EVE client's chat logs, parses intel, and serves it over a WebSocket. The
tablet app shows a live feed, jump distance from a chosen character, a region map, and raises
notifications for hostiles nearby.

Nothing leaves your network. There is no account, no cloud service, and no telemetry.

## Layout

| Module | What it is |
|---|---|
| `shared/` | Kotlin Multiplatform. Log format, intel parser, universe graph, wire protocol. Builds for JVM and Android. |
| `daemon/` | JVM app. Tails chat logs, deduplicates, tracks location, serves WebSocket. |
| `android/` | The APK. Feed, map, alerts. |
| `tools/` | SDE download and `universe.json` conversion. |

## Running the daemon

```
export JAVA_HOME="C:/Program Files/Android/Android Studio/jbr"
./gradlew :daemon:installDist
./daemon/build/install/daemon/bin/daemon.bat
```

It prints the address to enter on the tablet:

```
Listening on :31337
  tablet URL:  ws://192.168.1.50:31337/intel
```

Configuration lives in `eveintel.properties` beside the working directory:

- `intel.channels` — your intel channels, comma separated. `Local` is always tailed as well,
  because it is how character location is tracked.
- `scope.regions` — the regions the channel covers, from the channel MOTD. This is what lets `9UY`
  resolve unambiguously to `9UY4-H`.
- `server.port` — default 31337.

### Checking the parser against your own logs

```
./daemon/build/install/daemon/bin/daemon.bat --validate --limit 60
```

Replays recent history and prints what it understood, plus everything it failed to classify. This
is the intended way to tune `Vocabulary.kt` for your alliance's habits — look at what lands in the
`NOT CLASSIFIED AS INTEL` list and add the surface forms you actually use.

## Building the app

```
./gradlew :android:assembleDebug
adb install -r android/build/outputs/apk/debug/android-debug.apk
```

Then open Settings in the app, enter the host and port the daemon printed, and pick a character to
follow.

## How it works

**Log format.** EVE writes UTF-16LE files to `Documents/EVE/logs/Chatlogs`, named
`<Channel>_<date>_<time>_<characterId>.txt`, with a header block and lines like:

```
[ 2026.09.17 17:32:58 ] Quiet mantis > 1GH-48  Varro Kaine (Cyclone)
```

The daemon polls rather than using a filesystem watcher, because EVE holds its log files open and
Windows does not refresh directory metadata for open handles. It reads size from an open channel
and tracks a byte offset per file.

**Deduplication.** With several characters logged in, every channel line lands in several log
files. Messages are keyed on a hash of timestamp, author and text, so the tablet sees each once.

**Location.** The `Local` channel logs `EVE System > Channel changed to Local : 8DL-CP` on every
jump. That is where jump distances are measured from — no ESI, no SSO, no API keys.

**Parsing.** Two or more spaces separate entities in intel channels:

```
5-9L3H*  Halvard  Petra Vance  KrenV  Exequror Navy Issue* 3  Exequror* 1
```

The parser works segment by segment, matching each as a whole before scanning inside it. System
abbreviations resolve by prefix, but only for tokens containing a digit or hyphen — which every
nullsec name has, and which keeps ordinary English words from becoming systems.

**Universe data.** `universe.json` is built from CCP's SDE (via Fuzzwork's CSV dumps) by
`tools/build_universe.py`: 5485 k-space systems, 6989 stargate pairs, 423 ship types, about 1 MB.
Both the daemon and the tablet hold the same graph, so the wire carries only system ids.

## Security

The WebSocket has no authentication and no TLS. This is deliberate for a LAN service carrying data
that is already public to everyone in the channel. **Do not port-forward it.** If you need access
from outside the house, put both devices on a Tailscale tailnet rather than opening a port.

## Licensing

This is independent work. It does not contain code from RIFT or any other third-party tool — the
log format was determined by inspecting real log files, and the universe data comes from CCP's
published SDE. EVE Online and all related material are the property of CCP hf.
