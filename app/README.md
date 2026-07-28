# EveDeck (app source)

This is the source for the EveDeck desktop app. For what EveDeck does, how to install it, and
full setup instructions, see the [project README](../README.md) or [evedeck.space](https://evedeck.space).
This file covers building from source only.

## Safety policy

EveDeck only manages OS-level window placement, border style, and focus. It does not:

- Broadcast or multiplex input across clients.
- Forward keyboard or mouse input into an EVE client.
- Automate gameplay or login.
- Read or modify EVE client memory, or inject anything into it.
- Store EVE Online passwords.

Every hotkey action passes a safety guard (`Services/SafetyGuard.cs`) that blocks
input-forwarding behavior by construction — see [COMPLIANCE.md](COMPLIANCE.md) for the full
EULA boundary.

## Requirements

- Windows 10 (build 19041+) or Windows 11.
- .NET 10 SDK with the Windows Desktop workload, for building from source.
- EVE Online, for real use.

No admin rights are required for normal use, unless EVE itself is running as Administrator (in
which case EveDeck needs to run as Administrator too, or its global hotkeys can't reach the game).

## Build

```powershell
dotnet build .\EveDeck.sln
dotnet test .\EveDeck.sln
dotnet run --project .\src\EveDeck\EveDeck.csproj
```

Self-contained publish (what releases ship — no .NET runtime required to run the result):

```powershell
dotnet publish .\src\EveDeck\EveDeck.csproj -c Release --self-contained -r win-x64 -o .\publish
```

## Settings and logs

```text
%LOCALAPPDATA%\EveDeck\settings.json
%LOCALAPPDATA%\EveDeck\logs
```
