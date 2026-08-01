# Avalonia Migration Plan

Two phases. Design ground truth lives in [DESIGN-REFERENCE.md](DESIGN-REFERENCE.md) —
treat it as the acceptance spec, not inspiration.

## Branch workflow

All migration work happens on the **`avalonia-migration`** branch (started 2026-08-01,
Avalonia **12.1.1**). `main` holds the shipping WebView2 app. Merge/push to `main`
only when local builds are mature (parity checklist green + the §2.3 acceptance pass).
CI keeps building `main` untouched until then.

## Non-negotiable design constraints (decided up front)

1. **Square law.** One constant `UNIT = 54` (logical px). Channel cell = 1×1 unit,
   device tile = `inChannels` × `outChannels` units — every unit square, spans free.
   All geometry (tiles, headers, chips, hit-testing) derives from UNIT; UI-scale presets
   multiply it. The invariant lives in exactly one place: `MatrixLayout`.
2. **Corner square.** Source-label column width == destination-header height. One
   `LabelSquare` value (clamped 140–360), both resize handles write it.
3. **No OS title bar.** The header row IS the chrome: `ExtendClientAreaToDecorationsHint`
   + `NoChrome`, `BeginMoveDrag()` on header empty space, double-click maximizes,
   custom — ▢ ✕ buttons (✕ keeps minimize-to-tray).
4. **Dock widths.** `fixed | * | fixed | * | fixed` — flexible width goes to the
   source/destination level cards; metric boxes never grow.
5. **Lighting model everywhere.** `--fx-edge` two-stroke key lighting, 120ms
   `cubic-bezier(0.2,0.85,0.25,1)` motion, 70ms linear meters — per the design doc.

---

## Phase 1 — Core extraction (no visible change, ships as v0.3.x)

Goal: make the engine UI-agnostic so both hosts can run it. Low risk, valuable even if
the migration stalls.

1. New project `AudioMatrixRouter.Core`: move `Audio/*`, `Models/AppConfig.cs`, and the
   updater glue. Zero WinForms references.
2. Lift the contract that already exists in `MainForm` into Core:
   - `UiState` / `MetricsState` / `RouteState` DTOs (today they're private classes that
     get JSON-serialized — they become the ViewModel inputs).
   - An `AppController` class owning what `MainForm` does today minus WinForms: config
     load/save + in-memory `_lastSavedConfig`, known-device settings re-application,
     dormant seeding, startup retry/backoff policy, `TryAutoStart`, device-batch
     `setCrosspoints` orchestration (with `routeErrors`), rev stamping, Velopack
     check/download/apply. `MainForm` becomes a thin adapter: timers + WebView bridge
     → `AppController`.
   - Threading contract stays "single UI thread + marshal-from-COM-callbacks"; the
     controller takes an `Action<Action> marshal` (WinForms `BeginInvoke` today,
     `Dispatcher.UIThread.Post` tomorrow).
3. Ship it as a normal update. If anything regressed, it's found while the UI is still
   the battle-tested one.

**Exit criteria:** WebView2 app byte-for-byte behavior on the Phase-0 verification
scenarios (route survival, hotplug, boot retry, updater).

## Phase 2 — Avalonia UI, parity or bust (ships as v0.4.0)

New project `AudioMatrixRouter.App` (Avalonia 11, CommunityToolkit.Mvvm), referencing
Core. The WinForms project stays in the repo until exit criteria pass, then dies.

### 2.1 Architecture
- `MainViewModel` owns an `AppController`; engine events arrive already-marshaled via
  `Dispatcher.UIThread`. No serialization anywhere.
- `MetricsViewModel` updated by a 100ms `DispatcherTimer` → raises one change
  notification consumed by custom-drawn controls (`InvalidateVisual`), NOT thousands of
  bindings.
- `ThemeService`: computes every `color-mix()` derivative in code, exposes
  `DynamicResource` brushes; presets (7×7×7×7×7) are data.

### 2.2 The four custom controls (the real work, in build order)
1. **`DrumControl`** — ribbed drum + recessed housing + LED strip (exact spec in design
   doc §3.2). Pointer drag/wheel with step accumulator. Reused for IN/OUT buffers and
   master gain. Build first: it's small, and it proves the lighting model + Skia
   rendering match the reference screenshots.
2. **`MeterBars`** — N glass-gradient bars, horizontal or vertical, 70ms-eased toward
   target values, drawn in `Render()`. Used by headers, dock cards.
3. **`MatrixControl`** — the whole tile grid as ONE control. Owns `MatrixLayout`
   (UNIT math), draws every tile state (§3.5 table) with cached brushes/geometries,
   arithmetic hit-testing (the same unit-index math the React UI now uses), hover
   path highlighting, drag-scroll, wheel-gain, context-flags. Virtualization is
   unnecessary — drawing a few hundred rounded rects is nothing; the win is zero
   layout/visual-tree cost.
4. **`HeaderStrip`** — row/col device headers + detached channel chips (§3.3/3.4),
   items-based since counts are small; meters via `MeterBars`; drag-to-reorder.
- Everything else (topbar/chrome, corner buttons, quick pickers, dock, update pill) is
  ordinary styled Avalonia controls.

### 2.3 Parity checklist (acceptance)
Device/channel views · square law incl. asymmetric spans · corner-square resize ·
hover path + selection dimming · phase-invert visuals · blocked hatching · gain
readouts · drum steps (0.5dB / 5ms) with middle-click reset · lock/mute/power ·
show-all toggle · input-mode cycle · quick pickers (all 5 preset axes) persist to the
same `UiPreferencesJson` keys (users keep their theme/order/labels) · tray icon +
minimize-to-tray + single-instance foregrounding + `--startup` · startup-at-boot
shortcut · update pill state machine · window placement persistence · side-by-side
screenshot review against `docs/design/*.png` for every component.

### 2.4 Ship & retire
- Same Velopack `packId` — v0.4.0 arrives as a normal auto-update; config
  (`%APPDATA%`) and UI prefs carry over untouched.
- Keep a `v0.3-webview` branch for one release as the rollback path.
- Delete WinForms project + WebUI + npm from CI; publish shrinks to
  `dotnet publish` + `vpk pack`.

### 2.5 Complete scope inventory (nothing ports by accident)

Everything the current app does, with its source of truth. Each item is either ported,
consciously dropped, or replaced — no third state.

**Window & shell** (`MainForm.cs`, `Program.cs`)
- Custom chrome per constraint #3; dark caption DWM P/Invoke **dropped** (obsolete).
- Window placement save/restore (`WindowConfig` X/Y/W/H, start-minimized final-close rule).
- Tray icon + styled menu (Show/Quit), minimize-to-tray on ✕, balloon tip, Explorer-restart
  re-registration (`WM_TASKBARCREATED` → Avalonia `TrayIcon` re-init).
- Single instance: mutex `AudioMatrixRouter.SingleInstance` + `ShowWindow` EventWaitHandle
  foregrounding — reuse verbatim in App `Program.Main`.
- `--startup` / `--minimized` args; startup-at-boot Startup-folder shortcut (WScript COM).
- `timeBeginPeriod(1)`, High process priority, `SustainedLowLatency` GC — copy as-is.

**Engine lifecycle** (`MainForm.cs` → `AppController` in Phase 1)
- Config load → `ApplyToEngine` → `TryAutoStart`; startup retry backoff {2,4,8,15,30,60}s.
- Hotplug: `DevicesChanged` marshal + 250ms debounce; `SyncDevicesWithSystem` +
  `ApplyKnownDeviceSettings`; save debounce 350ms; in-memory `_lastSavedConfig`.
- `setCrosspoints` orchestration: device batch, channel resolution + `routeErrors`,
  suppress-push window, rev stamping; user device removal semantics (dormant + KnownDevices
  pruning); `clearRoutes`; lock gate on every mutation.
- Metrics: 100ms tick → `MetricsState` (destructive peak sampling ONLY here).
- Updater: Velopack check/download(progress)/apply + `AMR_UPDATE_URL` override;
  `VelopackApp.Build().Run()` first in Main.

**UI behaviors** (`App.jsx` — keep the file as executable documentation until parity)
- Matrix: device/channel view; square law; optimistic toggle + authoritative revert;
  hover path highlight + selection dimming; wheel gain ±0.5dB; middle-click gain reset;
  right-click phase invert; blocked loopback-self tiles; drag-scroll with click
  suppression; smooth wheel scroll; gain readout ≥0.5dB.
- Headers: labels (merge-with-saved), drag reorder (mergeOrder semantics), double-click
  set master, MASTER edge bars occupying footprint, meters full-lane rounded, detached
  channel chips (half-tile rule), inactive dimming.
- Corner: power, lock, mute (transient, works when locked), show-all, reload,
  input-mode cycle, view toggle, IN/OUT drums (5ms steps, middle-click reset),
  master gain drum (0.5dB steps, ±60/+12 clamp).
- Dock: metric tiles (fixed width), source/destination cards per §3.6, route indicator
  (🡢/⮆/⏸), hovered-route fallback to master pair.
- Topbar: brand, version pill, update pill state machine, status line, quick pickers
  (background/accent/font/size/scale/startup), collapse behavior, Escape handling.
- Meters: 10Hz, 250ms staleness zeroing, auto-scale tracker (floor/peak per device),
  `shapeMeterLevel` pow(0.72) curve — port `autoScaleLevels` math exactly.
- Latency/jitter display smoothing: EMA α=0.1, 1.2ms step threshold, 900ms min update,
  6s null grace — port `updateLatencyDisplay`/`updateJitterDisplay` exactly.
- Error banner (transient 4s), lock hides editing affordances everywhere.
- Persistence: `UiPreferencesJson` — SAME keys (backgroundKey, accentKey, fontKey,
  fontSizeKey, uiScaleKey, inputBufferMs, outputBufferMs, masterGainDb,
  controlsCollapsed, showAllDevices, inputDeviceMode, powerOn, locked, inputLabels,
  outputLabels, inputMasterId, outputMasterId, viewMode, labelSizing, matrixByView,
  inputOrder, outputOrder) so users' setups survive the swap; localStorage fallback
  **dropped** (native has the config file).

**Consciously dropped:** WebView2 env flags, virtual host + cache busting, bridge
protocol + rev gating (single-process now), `__nativeBridgeInvoke` timeouts, React
StrictMode workarounds, web-audio fallback mode (`AudioMatrixManager`), npm/vite.

**CI (on merge):** remove Node/npm steps; `dotnet publish AudioMatrixRouter.App` +
same `vpk pack` (same packId!); portable zip keeps its asset name.

### Risk register
| Risk | Mitigation |
|---|---|
| Visual drift from the web version | DESIGN-REFERENCE.md is the spec; screenshot A/B every control before wiring behavior |
| Glyph rendering (emoji/dingbats) | icon `StreamGeometry` set from day one (§4 of design doc) |
| Regressing freshly-fixed engine behavior | engine untouched; Phase 1 ships separately so any regression bisects to the UI swap |
| Hidden UI behaviors lost in translation | the parity checklist is extracted from App.jsx behavior, not memory; keep App.jsx in the branch as executable documentation |
