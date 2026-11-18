# Hulp

Hulp is a live-performance helper for the REAPER DAW. It turns a REAPER project into a set of songs, drives a
record-then-loop workflow during playback, and streams the session state over OSC to a browser dashboard that you can
keep on a second screen or a phone.

## Components

| Project                   | Output                         | Purpose                                                                                                                      |
|---------------------------|--------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| `Hemisphera.Hulp.Plugin`  | `reaper_hulp.dll/.so/.dylib`   | Native (AOT) REAPER extension: song handling, looper, FX parameter monitoring, hardware controllers, OSC output.              |
| `Hemisphera.Hulp.WebApp`  | ASP.NET Core / Blazor server   | Receives the OSC stream and renders the live dashboard (`/dashboard`) and a compact phone view (`/phone`).                    |
| `Hemisphera.Hulp.Shared`  | Class library                  | Constants shared by both sides (number of parameter slots, tracks, events, songs).                                          |

`Dashboard.json` is an [Open Stage Control](https://openstagecontrol.ammd.net/) session with eight knobs for the FX
parameters of the selected track (see [OSC protocol](#osc-protocol)).

```
REAPER + reaper_hulp ──OSC/UDP──▶ Hulp WebApp (UDP :9000) ──HTTP :5000──▶ browser
        ▲   │                         ▲
        │   └── MIDI CC ──▶ APC64     └── REAPER's own OSC (/play, /record, /pause) on the same "Hulp" OSC device
        └────── MIDI CC ◀──
```

## Quick start

### Prerequisites

- REAPER (x64)
- .NET 10 SDK (plus the native AOT toolchain for the plugin: MSVC build tools on Windows, `clang` + `zlib1g-dev` on Linux, Xcode on macOS)

### 1. Build and install the plugin

The plugin must be **published** (native AOT); a plain `dotnet build` does not produce a loadable library.

```powershell
dotnet publish Hemisphera.Hulp.Plugin/Hemisphera.Hulp.Plugin.csproj -r win-x64 -c Release
Copy-Item Hemisphera.Hulp.Plugin/bin/Release/net10.0/win-x64/publish/reaper_hulp.dll "$env:APPDATA\REAPER\UserPlugins"
```

Restart REAPER. The actions listed under [REAPER actions](#reaper-actions) appear in the action list.

### 2. Create the "Hulp" OSC device in REAPER

The plugin does not have its own host/port setting. It looks up a REAPER control surface named **`Hulp`** and sends to
its device IP and port.

1. *Preferences → Control/OSC/web → Add → OSC (Open Sound Control)*
2. Device name: `Hulp`
3. Mode: *Configure device IP + local port*
4. Device IP: the machine running the WebApp (`127.0.0.1` when local); device port: `9000`
5. Pattern config: *Default* (the WebApp uses REAPER's standard `/play`, `/record` and `/pause` messages from this device)

If you change the device later, run **Hulp: Restart Engine** to reconnect.

### 3. Run the WebApp

```powershell
dotnet run --project Hemisphera.Hulp.WebApp -- --open
```

The app listens for OSC on UDP port `9000` and serves HTTP on `http://*:5000` (from `appsettings.json`). On startup it
logs the LAN address (e.g. `http://192.168.1.20:5000`); `--open` also opens that address in the default browser.
Note that `dotnet run` applies the launch profile (`http://localhost:5079`); add `--no-launch-profile` or run the
published executable to serve on port 5000 for other devices.

### 4. Prepare the project and play

Set up songs as described in the [plugin documentation](Hemisphera.Hulp.Plugin/README.md#project-layout), place the
cursor inside a song region and run **Hulp: Start**.

## Configuration

The plugin reads `~/hulp.json` (your user profile directory, e.g. `C:\Users\<you>\hulp.json`). The file is optional
and reloaded on change. A complete example:

```json
{
  "Engine": {
    "SafeMode": true,
    "FastModeIntervalMs": 5
  },
  "LooperSettings": {
    "LookaheadBeats": 1.0,
    "ContainerTrackName": "Songs"
  },
  "Devices": {
    "Apc64": {
      "MidiInputDeviceName": "MIDIIN4 (APC64)",
      "MidiOutputDeviceName": "MIDIOUT4 (APC64)"
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Error",
      "Hemisphera.Hulp": "Debug"
    }
  }
}
```

See the [plugin documentation](Hemisphera.Hulp.Plugin/README.md#configuration) for every option and the
[WebApp documentation](Hemisphera.Hulp.WebApp/README.md#configuration) for `appsettings.json`.

## REAPER actions

| Command ID               | Name                        | What it does                                                                                     |
|--------------------------|-----------------------------|--------------------------------------------------------------------------------------------------|
| `HULP_INIT`              | Hulp: Initialize            | Detects songs, activates the song under the cursor and sends its state to the WebApp.            |
| `HULP_START`             | Hulp: Start                 | Initializes, starts the looper and starts playback.                                              |
| `HULP_FOCUS`             | Hulp: Focus song            | Initializes and zooms the arrange view to the current song, cursor at its start.                 |
| `HULP_SELECT_NEXT`       | Hulp: Select Next Song      | Focuses the next song region.                                                                    |
| `HULP_SELECT_PREVIOUS`   | Hulp: Select Previous Song  | Focuses the previous song region.                                                                |
| `HULP_SELECT`            | Hulp: Select Song           | For a relative MIDI encoder: values ≤ 63 select the next song, values > 63 the previous one.     |
| `HULP_RESTART`           | Hulp: Restart Engine        | Reconnects the OSC transport to the `Hulp` OSC device.                                           |
| `HULP_RECONNECT_DEVICES` | Hulp: Reconnect devices     | Re-resolves the MIDI ports of all configured hardware devices.                                   |
| `HULP_DEBUG`             | Hulp: Print Debug Info      | Dumps the current song (tracks, selectors, areas) to the REAPER console at `Debug` level.        |
| `HULP_TOGGLE_SAFE_MODE`  | Hulp: Toggle safe mode      | Switches between safe mode (REAPER main thread) and fast mode (background thread, 5 ms); see the [plugin docs](Hemisphera.Hulp.Plugin/README.md#safe-mode). |

## OSC protocol

Messages sent by the plugin to the `Hulp` OSC device (indices are 1-based):

| Address                              | Arguments                         | Meaning                                                    |
|--------------------------------------|-----------------------------------|------------------------------------------------------------|
| `/hulp/song/curr`                    | region id, start (s), end (s)     | Current song region                                        |
| `/hulp/song/curr/name`               | string                            | Current song name                                          |
| `/hulp/beat`                         | string `"beat/numerator"`         | Beat position within the measure                           |
| `/hulp/qnpos`                        | int                               | Playhead/cursor position in quarter notes                  |
| `/hulp/time`                         | double                            | Playhead/cursor position in seconds (0.1 s resolution)     |
| `/hulp/track/curr/name`              | string                            | Name of the selected track                                 |
| `/hulp/track/curr/fx/{1-8}/name`     | string                            | FX parameter slot name                                     |
| `/hulp/track/curr/fx/{1-8}/value`    | double (0–1)                      | FX parameter normalized value                              |
| `/hulp/track/curr/fx/{1-8}/value/str`| string                            | FX parameter formatted value                               |
| `/hulp/track/{1-10}/name`            | string                            | Song track name                                            |
| `/hulp/track/{1-10}/sel` · `/mute` · `/solo` · `/recarm` | bool          | Song track state                                           |
| `/hulp/event/{1-24}/text`            | string                            | Upcoming event description                                 |
| `/hulp/event/{1-24}/pos`             | double                            | Event position in quarter notes                            |
| `/hulp/song/{1-20}/id` · `/name`     | int · string                      | Song list in project order (id -1 / empty name when unused) |

The WebApp additionally consumes REAPER's standard `/play`, `/record` and `/pause` (float, > 0.5 = on).

## Build and release

- `scripts/publish.ps1` publishes the plugin (win-x64) and the WebApp to `%OneDriveConsumer%\Hemisphera\Hulp`
  (or `_out/Hemisphera/Hulp` when OneDrive is not set) and removes `.pdb` files.
- The GitHub Actions workflow (`.github/workflows/build.yml`) runs on pushes to the `release` branch and uploads the
  plugin (`win-x64`, `linux-x64`, `osx-arm64`) and framework-dependent WebApp builds for the same platforms as artifacts.

## Further documentation

- [Plugin](Hemisphera.Hulp.Plugin/README.md) — project layout, looper, FX parameter slots, devices, configuration, logging
- [WebApp](Hemisphera.Hulp.WebApp/README.md) — pages, ports, configuration
- [`spec.md`](Hemisphera.Hulp.WebApp/spec.md) — original design brief for the dashboard
