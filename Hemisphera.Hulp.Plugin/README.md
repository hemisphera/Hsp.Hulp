# Hulp Plugin

`reaper_hulp` is a native REAPER extension written in C# and compiled with .NET native AOT. It

- treats every project region with a matching folder track as a **song** and activates only the current one,
- runs a **looper** that records designated areas during playback and copies the result into loop areas,
- monitors up to **8 FX parameter slots** on the selected track (linked through the *Hemisphera/Relativator* JSFX),
- mirrors those slots to hardware controllers (currently the **Akai APC64**),
- sends the session state as **OSC** to the [Hulp WebApp](../Hemisphera.Hulp.WebApp/README.md).

## Installation

```powershell
dotnet publish Hemisphera.Hulp.Plugin/Hemisphera.Hulp.Plugin.csproj -r win-x64 -c Release
```

Copy `bin/Release/net10.0/win-x64/publish/reaper_hulp.dll` to `%APPDATA%\REAPER\UserPlugins` and restart REAPER.
On Linux/macOS publish with `-r linux-x64` / `-r osx-arm64` and copy `reaper_hulp.so` / `reaper_hulp.dylib` to
REAPER's `UserPlugins` folder.

A plain `dotnet build` produces a managed assembly only; REAPER can load the plugin only from the AOT `publish` output.

### OSC output

The plugin sends to the REAPER control surface named **`Hulp`** (*Preferences → Control/OSC/web*), using that device's
IP and port. If no such device exists, a warning is logged and nothing is sent (state changes are discarded, not
queued). After changing the device, run **Hulp: Restart Engine** (`HULP_RESTART`); the full state is resent on every
(re)connect. Send failures (e.g. while the WebApp is not running) are logged once and do not stop the transport. The full message list is in the [root README](../README.md#osc-protocol).

## Project layout

Hulp relies on naming conventions in the REAPER project.

### Songs

A song is a **region** plus a **folder track with the same name** (case-insensitive) that is a direct child of the
container folder track (default name `Songs`, see `ContainerTrackName`):

```
Songs                ← container folder track
├── Intro            ← song root track, region "Intro"
│   ├── Bass
│   └── Keys
└── Outro            ← song root track, region "Outro"
    └── Drums
```

Regions without a matching track are ignored. On initialize, the song whose region contains the playhead/cursor is
activated: its tracks are unmuted, shown in the TCP and have their FX enabled. All other songs' tracks are muted,
hidden and FX-bypassed.

### Items inside a song

Media items on the song's tracks (within the region) are interpreted by their **active take name**:

| Take name        | Meaning                                                                                                   |
|------------------|-----------------------------------------------------------------------------------------------------------|
| `record:<name>`  | Recording area. The looper records into this item's time range.                                           |
| `loop:<name>`    | Loop area. Receives a copy of the recording from `record:<name>` on the same track.                       |
| `audio:<name>`   | Recorded audio belonging to the area `<name>` that starts at the same position (created by the looper).   |
| `select`         | Track selector. When the playhead passes the item start, the track is selected exclusively.               |

Empty items (no take) with **item notes** become song notes and are shown as events.

Whether an area is MIDI or audio is decided by the track's record mode: MIDI overdub/replace modes make it a MIDI area,
everything else an audio area.

## Looper

**Hulp: Start** (`HULP_START`) initializes the song under the cursor, starts the looper and starts playback. On
initialize, every recording area that begins after the current position is cleaned (MIDI events deleted, `audio:` items
removed, including those of its loop areas).

While playing, the looper checks the position on every scheduler pass — about 30 times per second in safe mode, every
5 ms in fast mode (see [Safe mode](#safe-mode)):

1. **Before a recording area** — when the playhead is within `LookaheadBeats` of a clean `record:` area, the time
   selection is set to that area and recording is toggled on at the next beat.
2. **Leaving a recording area** — when no further recording area is upcoming, recording is toggled off at the next
   beat. The finished area is then propagated:
   - *MIDI*: the MIDI events of the `record:` item are copied into every `loop:` item of the same name.
   - *Audio*: the newly recorded item is renamed `audio:<name>`, copied, and pasted at the start of every `loop:` item
     (trimmed to the loop item's length).
3. **Entering a MIDI area** — after a quarter of the area has passed, it is propagated to its loop areas once already,
   so loops follow along while you play.
4. **Track selectors** — the most recent `select` item at the playhead selects its track.
5. **End of song** — once the playhead passes the region end, the looper stops and stops playback. If playback is
   stopped manually for more than 3 seconds, the looper resets.

Song navigation (`HULP_SELECT_NEXT`, `HULP_SELECT_PREVIOUS`, `HULP_SELECT`) moves the cursor to the start of the
neighbouring region, activates that song and zooms to it.

## FX parameter slots

The monitored parameters of the **selected track** are defined via REAPER parameter modulation, with the
**Hemisphera/Relativator** JSFX as the link source:

1. Insert `Hemisphera/Relativator` on the track.
2. On each target FX parameter, open *Parameter modulation/MIDI link* and link it from a Relativator parameter.
3. The Relativator parameter index determines the slot: index 2 → slot 1, index 3 → slot 2, … index 9 → slot 8.

For every slot the plugin reports the target parameter's name (text before the first `/`), its normalized value and its
formatted value. Incoming controller values are written to the Relativator (source) parameter, so the modulation link
stays in charge of the target.

## Devices

Devices are optional and registered at startup only if their section exists under `Devices` in `hulp.json`. Adding or
removing a section therefore requires a REAPER restart; changed MIDI port names only need
**Hulp: Reconnect devices** (`HULP_RECONNECT_DEVICES`).

### Akai APC64

| Setting                | Description                                         |
|------------------------|-----------------------------------------------------|
| `MidiInputDeviceName`  | REAPER MIDI input name the APC64 sends on           |
| `MidiOutputDeviceName` | REAPER MIDI output name used to send values back    |

Slots 1–8 map to **CC 14–21 on MIDI channel 2**, in both directions. Values received from the APC64 set the parameter;
parameter changes (and track changes, which reset all eight CCs to 0) are sent back so the controller's LEDs follow.

The MIDI ports must be enabled for input/output in REAPER's MIDI device preferences.

## Configuration

Settings live in `~/hulp.json` (user profile directory). The file is optional and reloaded when it changes; looper
settings take effect on the next initialize, device port names on the next reconnect, `Engine` settings immediately.

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
      "Default": "Error"
    }
  }
}
```

### `Engine`

| Setting              | Default | Description                                                                                  |
|----------------------|---------|----------------------------------------------------------------------------------------------|
| `SafeMode`           | `true`  | `true`: run on REAPER's main thread (~30 Hz). `false`: fast mode on a background thread. See [Safe mode](#safe-mode). |
| `FastModeIntervalMs` | `5`     | Pass interval in fast mode.                                                                   |

### `LooperSettings`

| Setting                            | Default | Description                                                                                       |
|------------------------------------|---------|---------------------------------------------------------------------------------------------------|
| `LookaheadBeats`                   | `1.0`   | How many beats before a recording area recording is armed. Keep it below one measure.             |
| `ContainerTrackName`               | `Songs` | Name of the folder track that contains the song root tracks.                                      |
| `AutoSelectTrackForRecordingAreas` | `true`  | Reserved; not evaluated yet.                                                                      |
| `StopAtEndOfRegon`                 | `true`  | Reserved; not evaluated yet (the looper always stops at the end of the region).                   |

### `Devices`

| Section        | Device                       |
|----------------|------------------------------|
| `Devices:Apc64`| [Akai APC64](#akai-apc64)    |

### Logging

Log output goes to the REAPER console. Without a `Logging` section the .NET host default (`Information`) applies; set
levels globally or per category (namespace/class) in `Logging:LogLevel`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Error",
      "Hemisphera.Hulp.Plugin.Infrastructure.LooperState": "Debug",
      "Hemisphera.Hulp.Plugin.Devices": "Debug"
    }
  }
}
```

Available levels: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`.
**Hulp: Print Debug Info** (`HULP_DEBUG`) only produces output when `LooperState` logs at `Debug`.

## Source overview

| Path                                  | Contents                                                                  |
|---------------------------------------|---------------------------------------------------------------------------|
| `Plugin.cs`                           | Entry point, DI setup, action registration, unload                        |
| `Commands.cs`                         | REAPER action handlers                                                    |
| `Infrastructure/HulpScheduler.cs`     | Runs all REAPER work in passes: main thread (safe mode) or background thread (fast mode) |
| `Infrastructure/LooperState.cs`       | Song detection and the looper                                             |
| `Infrastructure/HulpMonitor.cs`       | Polls REAPER each tick and publishes state changes as OSC / to devices    |
| `Infrastructure/MidiInputPoller.cs`   | Polls REAPER's MIDI input for controller messages                         |
| `Infrastructure/OscTransport.cs`      | Sends queued OSC messages to the `Hulp` OSC device                        |
| `Models/Song.cs`, `Models/TrackArea.cs` | Song and record/loop area model, recording propagation                  |
| `Models/MonitoredParameterFactory.cs` | Resolves Relativator-linked FX parameters                                 |
| `Devices/`                            | Hardware controller integrations                                          |
| `Settings/`                           | Option classes bound from `hulp.json`                                     |

## Safe mode

REAPER's API is meant to be called from its main thread only, and ReaSharp does not marshal calls (it even runs action
handlers on thread-pool threads). All of the plugin's REAPER work — actions, the monitor, the looper, MIDI input
polling and `await` continuations — therefore runs through `HulpScheduler`, one pass at a time. Where the passes run
depends on the mode:

| Mode                     | Runs on                                   | Pass rate        |
|--------------------------|-------------------------------------------|------------------|
| Safe mode (default)      | REAPER's main thread (REAPER `timer` callback) | ~30 per second (~33 ms) |
| Fast mode                | A dedicated background thread             | every `FastModeIntervalMs` (5 ms) |

Fast mode is how earlier versions worked: finer looper timing, but REAPER is called off its main thread, which can in
rare cases corrupt edits or crash REAPER. In both modes passes never overlap, so the plugin's own code does not race
with itself.

Switching modes takes effect immediately, without restarting REAPER:

- set `Engine:SafeMode` in `hulp.json`, or
- run **Hulp: Toggle safe mode** (`HULP_TOGGLE_SAFE_MODE`), e.g. bound to a key or controller button. This action
  bypasses the scheduler, so it works even if passes are stuck. The toggle lasts until `hulp.json` changes or REAPER
  restarts.

The current mode is logged as a warning on every switch.

### Rules for code

- Touch REAPER only from scheduler passes: subscribe to `HulpScheduler.Tick`, or run work with `Post` / `InvokeAsync`.
  Actions are wrapped automatically (`Plugin.RegisterCommand`).
- `await` in pass code resumes in a later pass. Never block a pass on such a task (`.Wait()`, `.Result`): that deadlocks.
- Only the OSC send loop runs outside the scheduler; it does not call REAPER.
- Do not use ReaSharp's `MidiListener` or `TrackMediaItemWatcher`: both poll REAPER from thread-pool threads.
