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

- Desktop host: WinForms (.NET 9) with WebView2.
- Audio engine: NAudio + WASAPI device enumeration and mixing.
- UI: React + Vite.
- Config persistence: JSON file beside the executable.

Core project folders:

- AudioMatrixRouter/Audio: audio engine, routing matrix, mixing, ring buffer.
- AudioMatrixRouter/WebUI: React frontend.
- AudioMatrixRouter/Models: config models and serialization.
- .github/workflows: CI, Pages, and release automation.

## Requirements

- Windows 10/11.
- .NET 9 SDK for building from source.
- Node.js 20+ and npm for WebUI builds.
- WebView2 Runtime installed (normally present on modern Windows).

## Quick Start (Build And Run)

```powershell
git clone https://github.com/drajabr/audio-matrix-router.git
cd audio-matrix-router
./build-all.ps1
./build/desktop/AudioMatrixRouter.exe
```

The build script outputs:

- build/web: static web build (Pages mode).
- build/desktop: desktop app publish output.

## Run In Development

Frontend only:

```powershell
cd AudioMatrixRouter/WebUI
npm ci
npm run dev
```

Desktop app with local build output:

```powershell
dotnet run --project AudioMatrixRouter
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
- Startup at boot is controlled from the app and stored in the Windows Run registry key.
- Supported startup args:
	- --startup
	- --minimized

## Configuration

App state is saved to:

- config.json next to AudioMatrixRouter.exe

Saved fields include:

- Window position/size and start-minimized preference.
- Selected input and output devices.
- Active crosspoints and gain values.
- Lock state.

## CI/CD

- Build and release workflow: .github/workflows/ci.yml
- Pages deployment workflow: .github/workflows/pages.yml

GitHub Pages base path is resolved from repository name during CI so assets load correctly for current and renamed repos.

## Troubleshooting

- Git add fails with Cookies permission denied:
	- Close the running app first so WebView2 file locks are released.
	- The build output folders should be ignored by git.

- Pages site loads but JS/CSS 404 or wrong MIME type:
	- This means the built base path does not match the repository Pages path.
	- Re-run the Pages workflow after the latest workflow and Vite config updates.

- No audio after device changes:
	- Refresh devices from the UI.
	- Re-check active crosspoints and gain values.

## Links

- Repository: https://github.com/drajabr/audio-matrix-router
- Releases: https://github.com/drajabr/audio-matrix-router/releases
- Pages preview: https://drajabr.github.io/audio-matrix-router/

## License

MIT License. See LICENSE.
