# Audio Router Matrix

Audio Router Matrix is a Windows desktop patchbay for WASAPI devices.
It lets you route any input channel to any output channel using a live crosspoint matrix, with per-route gain, phase inversion, and real-time level metering.

<img width="1086" height="743" alt="image" src="https://github.com/user-attachments/assets/83e1af22-4f78-44dd-968d-9a4009eb53f1" />


## What This Tool Does

- Builds a channel-level routing matrix across selected input and output devices.
- Mixes multiple sources into the same destination channel when multiple crosspoints are enabled.
- Applies gain per crosspoint in dB.
- Supports phase inversion controls from the UI.
- Shows live meters so you can monitor signal activity while routing.
- Persists device selection, routes, lock state, and window state to config.json.
- Runs in the system tray and can start with Windows.

## Typical Use Cases

- Route a multi-channel interface input into a different hardware output map.
- Build quick monitor mixes without opening a DAW.
- Repatch channels between USB interfaces for streaming, recording, or testing.
- Keep a stable routing setup that restores automatically on restart.

## Architecture

- Desktop host: Avalonia 12 (.NET 10), single process, custom-drawn matrix UI.
- Audio engine: NAudio + WASAPI device enumeration and mixing (UI-agnostic Core project).
- Config persistence: %APPDATA%\AudioMatrixRouter\config.json.

Core project folders:

- AudioMatrixRouter.Core: audio engine, routing matrix, mixing, ring buffer, app controller, config models.
- AudioMatrixRouter.App: Avalonia UI (custom controls: matrix, drums, meters), tray, updater.
- docs/: design reference (with screenshots) and migration notes.
- .github/workflows: CI and release automation.

## Requirements

- Windows 10/11.
- .NET 10 SDK for building from source (no Node.js required).

## Quick Start (Build And Run)

```powershell
git clone https://github.com/drajabr/audio-matrix-router.git
cd audio-matrix-router
./build.ps1
./build/desktop/AudioMatrixRouter.App.exe
```

(`build.cmd` is a double-clickable elevated wrapper for the same script.)

## Run In Development

```powershell
dotnet run --project AudioMatrixRouter.App
```

## How Routing Works

- Rows are global input channels.
- Columns are global output channels.
- Each active tile represents one enabled crosspoint from input channel to output channel.
- Multiple active tiles targeting the same output channel are mixed.
- Gain is stored per crosspoint and restored from config.
- Route edits can be locked to prevent accidental changes.

## Startup And Tray Behavior

- Closing the window minimizes to tray by default.
- You can quit from the tray menu.
- Startup at boot is controlled from the app via a shortcut in the user's Startup folder.
- Supported startup args:
	- --startup
	- --minimized
	- --no-watchdog (disable the keep-alive watchdog for this launch)

## Keep-alive and health

- The app spawns a watchdog (a second instance of the same exe in `--watchdog` mode). It relaunches the app
  after a crash or when the engine heartbeat stops for 10 s, with a crash-loop guard. Disable it with
  `--no-watchdog` or `"keepAlive": false` in config.json.
- The engine runs a 1 s health tick: endpoints that stop consuming are faulted and recovered with backoff,
  and underruns raise the ring headroom automatically (see LATENCY below).
- The process opts out of Windows 11 power throttling so timer resolution and scheduling stay honest while
  the window is hidden in the tray.
- Diagnostics: `%TEMP%\amr-wasapi-probe.log` (rotates at 1 MB) records every start with the latency budget,
  faults, stalls, master promotions and auto-headroom changes.

## Updates

- Install via the Setup.exe from GitHub Releases (installer/updater flow only — no portable zip).
- The app checks for updates via the version pill (top-left); updates download from GitHub Releases and install silently on restart (Velopack, with delta packages).
- Installed builds live in %LocalAppData%\AudioMatrixRouter.

## Configuration

App state is saved to:

- %APPDATA%\AudioMatrixRouter\config.json (a legacy config.json next to the exe is read once and migrated)

Saved fields include:

- Window position/size and start-minimized preference.
- Selected input and output devices.
- Active crosspoints and gain values.
- Lock state.

## CI/CD

- Build and release workflow: .github/workflows/ci.yml
- Releases publish a Velopack Setup.exe + update packages (installer/updater only).

## Troubleshooting

- No audio after device changes:
	- Refresh devices from the UI (↻ in the corner block).
	- Re-check active crosspoints and gain values.

- Latency: one LATENCY drum. Its lowest position is AUTO: the engine runs at the floor the devices allow
  (about 54 ms end to end on 10 ms shared-mode endpoints) and adds headroom by itself when an input underruns,
  then takes it back after five clean minutes, never below the level that last caused a storm in this session.
  Turn the drum up for a manual end-to-end target. The caption always shows the effective value; a trailing +
  means auto headroom is in effect.
  - The capture term of the budget is MEASURED: each input's worst recurring delivery gap (virtual cables
    deliver in ~16 ms bursts behind a 10 ms period), so the floor is honest per input and every input holds
    its own ring target. Loopback inputs are event-driven with a silent keep-alive stream; polling is the fallback.
  - ⚡ Exclusive mode (off by default): outputs and microphones open exclusive-mode streams at the driver's
    minimum period (3 ms on most devices instead of 10 ms), which roughly halves the end-to-end latency. While
    routing, other apps cannot play to those outputs and Windows volume does not apply (the matrix gain is the
    only level). An output that feeds a loopback input stays shared, and exclusive is all-or-nothing across
    outputs so they keep equal FIFO depth; the key's tooltip says why it is not in effect when it is not.
    Requires "Allow applications to take exclusive control" in the device's Sound settings (the Windows default).

- Underruns counting up:
	- Leave LATENCY on AUTO and let the headroom loop settle, or raise it manually. Check the log for
	  `auto headroom` and `safety tier` lines to see what the engine did.

## Links

- Repository: https://github.com/drajabr/audio-matrix-router
- Releases: https://github.com/drajabr/audio-matrix-router/releases
- Pages preview: https://drajabr.github.io/audio-matrix-router/

## License

MIT License. See LICENSE.
