# Hulp WebApp

A Blazor Server app that receives the OSC stream from the [Hulp plugin](../Hemisphera.Hulp.Plugin/README.md) (and
REAPER's standard transport messages) and shows it as a live dashboard for stage use.

## Running

```powershell
dotnet run --project Hemisphera.Hulp.WebApp            # or: dotnet run --project Hemisphera.Hulp.WebApp -- --open
```

| Endpoint | Value                                  | Where it is set                               |
|----------|----------------------------------------|-----------------------------------------------|
| OSC in   | UDP `9000`, all interfaces             | Hard-coded in `Services/OscService.cs`        |
| HTTP     | `http://*:5000`                        | `Kestrel:Endpoints` in `appsettings.json`     |

On startup the app logs the address it can be reached at from the LAN (e.g. `WebApp is running at: http://192.168.1.20:5000`).
With `--open` it also opens that address in the default browser.

> `dotnet run` uses `Properties/launchSettings.json`, whose profiles listen on `http://localhost:5079` (and
> `https://localhost:7222`) in the `Development` environment. Use `--no-launch-profile` or the published executable to get
> the `appsettings.json` endpoint that is reachable from other devices.

In REAPER, point the **`Hulp`** OSC device at this machine, port `9000`, with the *Default* pattern config (see the
[root README](../README.md#2-create-the-hulp-osc-device-in-reaper)).

## Pages

### `/dashboard` (also `/`)

Full-screen view for a monitor next to the stage:

| Area             | Content                                                                                                             |
|------------------|---------------------------------------------------------------------------------------------------------------------|
| Header           | Current song name                                                                                                   |
| Tracks (top left)| Up to 8 tracks of the current song. Empty slots and muted tracks are greyed out; soloed, record-armed and selected tracks are highlighted. |
| Upcoming (top right) | Upcoming events (recording areas, track selectors, song notes) that lie after the current position. The next event shows a countdown in quarter notes (`+++` when more than 16 away) with a progress gauge. |
| FX Parameters (bottom left) | Selected track name and the 8 FX parameter slots: name, formatted value, percentage and fill bar. |
| Transport (bottom right) | Play / record / pause / stop indicator and the time remaining in the current song region. |

### `/phone`

Compact portrait layout for a phone: song and track name in a header coloured by transport state (idle / playing /
recording) and the 8 FX parameter cards.

## Behaviour notes

- FX values (`/value`, `/value/str`) are debounced per slot: at most one UI update every 100 ms, always with the latest
  value. Names and the track name update immediately.
- The event list and the transport countdown also refresh once per second.
- All state lives in the singleton `OscService`; every connected browser sees the same data.

## Configuration

`appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "http": {
        "Url": "http://*:5000"
      }
    }
  }
}
```

Change `Kestrel:Endpoints:http:Url` to serve on a different port. The OSC port can currently only be changed in code.

## Source overview

| Path                               | Contents                                                       |
|------------------------------------|----------------------------------------------------------------|
| `Program.cs`                       | Host setup, LAN address logging, `--open`                      |
| `Services/OscService.cs`           | OSC listener and in-memory state, change events for the UI     |
| `Services/FxValueDebouncer.cs`     | Per-slot 100 ms value debouncing                               |
| `Components/Pages/Home.razor`      | `/dashboard` layout                                            |
| `Components/Pages/Phone.razor`     | `/phone` layout                                                |
| `Components/Dashboard/`            | `TrackView`, `EventView`, `FxParameterMatrix`, `Transport`     |
| `wwwroot/app.css`                  | Global styles and dashboard grid                               |
