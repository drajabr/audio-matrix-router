# Audio Matrix Router — Fix Plan (toward v0.5)

Status: PLAN ONLY. No source file has been changed. Every item below names the file and
line range it touches so it can be implemented and reviewed one at a time.

Ground truth used for this plan:

- Every source file in `AudioMatrixRouter.Core` and `AudioMatrixRouter.App` (11,951 lines),
  `MainWindow.axaml`, both csproj files, build script, README and the design docs.
- The live config at `%APPDATA%\AudioMatrixRouter\config.json`.
- The engine diagnostic log at `%TEMP%\amr-wasapi-probe.log` (1,247 lines).
- A clean `dotnet build` of the solution (0 warnings, 0 errors) on .NET SDK 10.0.201.

---

## 1. The setup this plan is tuned for (observed, not assumed)

| Fact | Source |
|---|---|
| Input: "Microphone (Steam Streaming Microphone) (VB-Audio Virtual Cable)", 48 kHz, 2 ch, fixed 10 ms period (min = default = 480 frames). Software-clocked virtual device; delivers in bursts and is silent whenever Steam is not streaming. | config, probe log |
| Outputs: Mi TV + two SHARP HDMI (NVIDIA HDMI), all 48 kHz 2 ch, fixed 10 ms period. Displays sleep independently. | config, probe log |
| Config holds **18 output entries / 15 known devices** with duplicate names: 6× "SHARP HDMI Left", 5× "SHARP HDMI Right", 4× "Mi TV". NVIDIA re-creates the endpoint with a **new ID** each time a display reconnects. | config |
| Knobs: LATENCY = 20, IN = 40, locked = true, start at boot, input mode = input, show-all on, channel view. | config |
| **208 endpoint faults** logged, 207 of them `0x88890004` (AUDCLNT_E_DEVICE_INVALIDATED), all on the NVIDIA HDMI endpoints, in bursts of 3–4 at the same second (one display event kills every HDMI endpoint at once). | probe log |
| After each fault burst the log shows **three engine starts within ~1 s** (three `ProbeEnginePeriods` entries), i.e. every fault triggers 2–3 full restarts. | probe log, e.g. 14:47:50 → 14:47:53/53/54 |

---

## 2. Symptom → root cause map

| Your symptom | Root causes (section 3) |
|---|---|
| Working / hanging in the background | RC-8 (engine runs on the UI thread with unbounded waits), RC-5 (restart storms), RC-14 |
| Dissipation (audio degrades / breaks up) | RC-1 (repeated frame every 10 ms + pinned servo), RC-5, RC-4 (channel/rate mismatch after HDMI renegotiation), RC-2 |
| Sync drops | RC-5 (every restart re-anchors all outputs with ~1 s of silence), RC-3 (stalled master), RC-12 |
| Devices not recovered | RC-9 (new endpoint IDs), RC-10 (no retry after a failed start / stage limit), RC-5 |
| IN / LATENCY have no effect | RC-7 |
| Lowest latency is not low | RC-7 |
| Draining buffers | RC-1 (chronic surplus → sustained hard drain every ~2–3 min), RC-3 (drain every second when the master stalls), RC-6 |
| Pitch shift | RC-4 (48 k content into a 44.1 k stream = −8.8 %), RC-1 (servo pinned at −2000 ppm plus a 100 Hz frame-repeat artefact) |
| Underruns | RC-2, RC-11, RC-7 (ring floor too thin for a bursty virtual input), RC-14 |

---

## 3. Root causes

Each entry: where, what happens, how it was established, fix summary. "Verified" means traced
in the code or seen in the log; "reasoned" means derived from the code but not observed live.

### RC-1 — Input ASRC emits one extra frame per capture callback at unity ratio (verified by trace)

`AudioMatrixRouter.Core/Audio/InputAsrc.cs:218-224`

After the resampling loop the cursor overshoot is wrapped with `if (overshoot >= 1.0) overshoot -= floor(overshoot)`.
At ratio 1.0 the loop ends with `s = captureFrames`, overshoot is exactly 1.0, it is wrapped to 0, and the
next block starts at `s = -1`, which re-emits the previous block's last frame. Net effect per 10 ms block:
`captureFrames + 1` output frames for `captureFrames` input frames, a constant **+2083 ppm surplus**.

Consequences:
- The fill servo saturates at its −2000 ppm cap (`MaxTrimPpm`), leaving ~+83 ppm uncorrected, so ring fill
  creeps ~4 frames/s and the sustained hard drain (`UpdateServo`, lines 292-301) trips roughly every 2–3 minutes:
  an audible skip of ~12 ms on every output. This is the periodic "draining buffers".
- A duplicated frame every 10 ms is a 100 Hz artefact on all routed audio.
- The pitch is permanently shifted by −0.2 % (small, but it compounds with everything else).

The same wrap also drops source frames whenever `step > 1` (capture rate above engine rate, e.g. 96 k → 48 k).

Fix: delete the wrap. `_srcFrac = overshoot` with overshoot in `(0, step]` is the correct carry; the comment on
line 68 ("in [0,1)") is wrong and goes too. Add a unit test: N blocks at ratio 1.0 produce exactly N×frames.

### RC-2 — A source that starts (or resumes) after the barrier is starved for seconds (verified by trace)

`AudioMatrixRouter.Core/Audio/MixingSampleProvider.cs:843-859` and `:918-948`

While a source delivers nothing, each block books the shortfall as a **pending skip**. When the source starts
(Steam begins streaming into the cable), every block first skips exactly the data that just arrived, so the ring
never accumulates. Recovery only happens through the servo's +2000 ppm trim: ~5 s of silence, then ~20 s of
starvation-level crackle until fill reaches target. The Steam microphone is silent at every engine start, so this
happens on every start and every stream start.

Fix: treat "producer resumed after idle" as a join. The ring tracks its producer liveness (last write tick); when
the producer resumes after > 3× its cadence of silence, the ring re-anchors **every** consumer cursor on that ring
to `writePos − targetFill` and bumps a per-ring generation counter. Providers see the generation change and clear
`_pendingSkipPerSource` for that source. All consumers move identically, so inter-output phase is untouched.

### RC-3 — A stalled master output poisons every other output (verified)

- `WasapiRenderClient.cs:166-185`: since commit 7dfffcc a render stream that stops consuming but still answers
  `GetCurrentPadding` with S_OK is never faulted. `frames = bufferFrames − padding` is 0 forever and the loop spins
  on the 20-period timeout. A sleeping HDMI display behaves exactly like this before (or instead of) invalidating.
- `OutputSyncCoordinator.ReportConsumedAndUpdate` (`MixingSampleProvider.cs:349-364`) freezes follower trim when
  the master is stale, but nothing promotes a new master.
- `InputAsrc.UpdateServo` (`:269-274`) measures ring fill at the **master's** consumer cursor
  (`ApplyPreferredMasterConsumerToInputs`, `AudioEngine.cs:1567-1587`). A dead master cursor reads as ever-growing
  fill: the servo pins −2000 ppm, and the sustained drain fires **every second**, skipping 12 ms+ on every healthy
  output. DROPS climbs because the UI reports the master consumer's overflow drops (`AppController.cs:584-586`).

Fix (three parts):
1. Render client faults on a real stall: padding > 0 and unchanged across ≥ 1 s of timeouts (a consuming shared
   stream must decrease padding every period, so this cannot misfire on a quiet-but-alive driver, which was the
   reason for 7dfffcc).
2. Coordinator promotes the freshest live follower to master when the master's report age exceeds 300 ms
   (`PromoteMasterNoLock`), without resetting timelines; raises `MasterChanged`.
3. `InputAsrc` resolves its fill consumer from the coordinator's current master each update (volatile read)
   instead of a fixed string, so the servo always follows a live cursor. The ring's preferred consumer follows too.

### RC-4 — Render format drift is unguarded (verified)

`AudioEngine.cs:1302-1352` (`TryInitRender` / `AdoptRender`) versus `WasapiRenderClient.FinishInit` (`:410-412`).

The custom render client opens the endpoint at its **live** mix format (`ActualSampleRate`, `ActualChannels`),
but the `MixingSampleProvider` is built from `dev.Info.SampleRate/Channels` captured when the device was added,
and `_engineSampleRate` comes from the same stale record. The capture side has a guard (`:1403-1416`); the render
side has none. HDMI endpoints renegotiate their format on reconnect/wake:
- rate changes (48 k → 44.1 k) play 8.8 % slow and flat: **pitch shift**;
- channel-count changes (2 → 8 for an eARC/receiver) mis-interleave every sample: noise and wrong speed.

Fix: after `Init`, if actual ≠ enumerated, refresh `dev.Info` with the actual format and rebuild the provider
(and, if this is the master, restart once around the new engine rate). Handle `IMMNotificationClient.OnPropertyValueChanged`
for `PKEY_AudioEngine_DeviceFormat` (`DeviceEnumerator.cs:56`) as a device change so a live format edit in Sound
settings restarts only that device.

### RC-5 — Fault → restart storm (verified from the log)

`AppController.cs:96-109, 371-387`, `AudioEngine.cs:1630-1733`.

One `DEVICE_INVALIDATED` fault → 300 ms debounce → `ReloadEngine`: `SyncDevicesWithSystem` →
`Engine.RefreshDevices()` (which itself Stops/Starts when the device list changed) → unconditional `Stop()` →
`TryAutoStart()`. The same invalidation also fires `DevicesChanged` → `OnDeviceRefreshTick` → another
`RefreshDevices` → possibly another Stop/Start. Result: 2–3 full restarts per fault (log: three starts in ~1 s).
Each restart silences **every** output for the barrier duration, re-anchors phase, and resets all counters. When the
HDMI endpoint is still enumerated but not ready (NVIDIA during handshake), the next start re-opens it, it faults
again, and the cycle repeats every ~1–2 s: that is the "sync drops"/"dissipation" during display events, and a large
share of the UI hangs (each restart blocks the UI thread, RC-8).

Fix:
- Coalesce: a single `RequestRestart(reason)` path with a per-reason cooldown; `ReloadEngine` never performs its
  own Stop when `RefreshDevices` already restarted.
- Per-device fault backoff / quarantine: a device that faults twice within 10 s is excluded from the next start for
  15 s, then 30 s, then 60 s (cap). Healthy outputs keep playing. A quarantined device rejoins **without** a global
  restart once Phase 3.7 (incremental join) lands; until then, it rejoins on the next scheduled retry.
- Fault handling and all restarts move to the engine thread (RC-8).

### RC-6 — Overflow "payback" re-overflows the ring (verified by trace)

`MixingSampleProvider.cs:831-841, 928-936`; `RingBuffer.Write` (`:194-253`).

When the ring force-advances a lagging consumer, the provider records a **negative** pending skip and then
deliberately under-consumes to walk the cursor back to its timeline. For a consumer that lagged because its
device stalled, this re-fills the ring to capacity and overflows again next write, forever. That is the
"millions of dropped frames" burst the v0.4.5 commit message describes; parking unrouted outputs hid the common
trigger but the mechanism is still there for any stalled consumer.

Fix: a force-advance is a skip. Fold `forced` into the timeline (`ReportConsumedAndUpdate(..., engineFramesConsumed
+ forced, ...)`) and never under-consume. Phase between consumers is preserved because a consumer that is behind
has, in truth, skipped.

### RC-7 — The latency knobs are mostly dead and the render FIFO is always full (verified)

- `WasapiRenderClient.cs:140-144` pre-rolls the **whole** buffer with silence and `:180-185` tops it up to full on
  every event. FIFO depth = allocated buffer (≈ 21 ms on these endpoints for a 10 ms request), so render latency is
  31 ms regardless of the knob.
- `AudioEngine.cs:1589-1616`: ring fill target = `clamp(knob − cadence − knob/4, gulp + cadence + 5, 300)` where
  gulp = that full buffer. At knob 20 the floor wins: 21 + 10 + 5 = 36 ms. Everything below knob ≈ 60 collapses to
  the same ~77 ms total (10 capture + 36 ring + 31 render). The knob only starts to matter above ~60.
- IN (`SetInputBufferMs`, `:1278-1300`) only sets a cosmetic `CaptureLatencyMs` (overwritten at start) and the
  NAudio fallback buffer. On the custom client it does nothing except restart the engine. Config default is 40,
  drum default is 10 (`AppConfig.cs:87`, `MainWindow.axaml` InDrum).

Fix: see Phase 2. Realistic floor for 10 ms-period shared-mode endpoints after the fix: ≈ 10 (capture) +
≈ 22 (ring: 12 gulp + 10 headroom) + ≈ 22 (render: 12 padding + 10 engine period) ≈ **45 ms**, versus ~77 today,
and the knob is effective from there upward. Lower needs endpoints with small periods (the VB output supports
2.67 ms; the NVIDIA and VB capture sides do not) or exclusive mode (out of scope).

### RC-8 — All engine work runs on the UI thread with unbounded waits (verified)

- `WasapiRenderClient.Init` (`:66-80`) and `WasapiCaptureClient.Start` (`:62-75`) block on `_initDone.Wait()` with
  no timeout. NVIDIA HDMI activation during a display handshake can take seconds per endpoint.
- `Dispose` joins the audio thread for up to 2 s per endpoint (`:91-113`, capture `:83-101`); `Stop()` does this
  for every endpoint serially.
- `DeviceEnumerator.GetDevices` (`:19-43`) activates an `AudioClient` and reads `MixFormat` for **every** endpoint
  on every call; it is called 2× per `RefreshDevices`, again inside every `AddInputDevice/AddOutputDevice`, and in
  `EnsureAvailableDevicesCached`. `StartCore` adds two more activations via `ProbeEnginePeriods` (`:1102-1109`).
- `MainWindow()` (`MainWindow.axaml.cs:77-156`) calls `_controller.Initialize()` synchronously, so the whole engine
  start (and its device waits) runs inside the window constructor.
- `EvaluateAdaptiveDegrade` (`AudioEngine.cs:172-242`) calls `FullRestart()` on a **timer thread** while the UI
  thread may be inside `SetCrosspoints`/`RefreshDevices`: unsynchronised mutation of the device lists.

Fix: Phase 3.1 (engine thread) plus bounded waits. Until then the app "hangs" for the sum of every driver wait on
each fault, hotplug, knob change and start.

### RC-9 — Hotplug identity: NVIDIA re-creates endpoints with new IDs (verified from config)

Routes, dormant routes, known devices, delays, master preference and UI order/labels are all keyed by endpoint ID.
A display that reconnects with a new ID is a stranger: its routes stay dormant for the old ID forever, the old
entry is never pruned, and the config grows (6× "SHARP HDMI Left"). The user sees "device not recovered".

Fix: identity remap by (flow, name, channels) when an ID vanishes and a same-name device appears that is not
already active. Applied to dormant routes, known devices, output delays, master preferences and the UI prefs
(`inputOrder/outputOrder/inputLabels/outputLabels`). Name matching ordinal-ignore-case ("SHARP HDMI left" vs
"Left" is the same panel). Stale same-name duplicates are pruned from the config once remapped.

### RC-10 — No "ensure running" loop; power state not honoured (verified)

- `AppController.cs:286-308`: the boot retry stops after 6 stages (~2 min). A `Start()` that fails for any other
  reason (all outputs refused to open) is never retried until a device event happens.
- `TryAutoStart` (`:340-346`) ignores the power button; `SetEngineEnabled(false)` is undone by the next hotplug
  tick or fault. The persisted `powerOn=false` is never applied at launch (`MainWindow.ApplyPreferences` reads it
  but nothing calls `SetEngineEnabled`).

Fix: controller owns `Enabled`; a single `EnsureRunning()` with exponential backoff (1, 2, 4, 8, 15, 30 s, forever)
runs whenever routes exist, both device lists are non-empty, the engine is enabled and either it is not running or a
routed device is not started.

### RC-11 — Underruns are undercounted and counters reset on every restart (verified)

`MixingSampleProvider.cs:1137-1139`: an underrun is only counted when the producer wrote **in this very block**.
Jitter on a 10 ms capture against a 10 ms render block routinely yields blocks with zero production, so the exact
starvation the user hears is not counted. Counters live in objects that are recreated on every restart.

Fix: liveness from the ring (written within 3× cadence), not per-block production; counters move to `ActiveDevice`
and survive incremental joins; the tile shows a recent rate and the total in a tooltip.

### RC-12 — Master projection across a stall causes a skip followed by a hold (reasoned)

`MixingSampleProvider.cs:366-399`: the master position is projected by wall clock from its last report. A master
block that arrives > 20 ms late makes every follower see +20 ms, hard-skip, then (after the master's catch-up
report) −20 ms, and hold after the 2 s cooldown. Two audible glitches per master hiccup on every follower.

Fix: cap the projection at one master period; require the gross error to persist ≥ 3 consecutive blocks before a
hard re-anchor; raise the threshold to 30 ms. Account for per-device FIFO depth in the error (equal by construction
once Phase 2.1 fixes the padding target, so this becomes a no-op guard).

### RC-13 — Handle leaks on init failure (verified)

`AudioEngine.cs:1309-1321`: when `rc.Init()` throws, `dev.Render` is still null, so the client is never disposed
(three wait handles per attempt). With RC-5's storms this leaks hundreds of handles per hour.

Fix: dispose the client in the catch; add a 5 s init timeout that fails the device instead of waiting forever.

### RC-14 — UI allocation churn competes with the audio threads (verified by reading)

`MatrixControl.EnsureLayout` (`:238-244`) rebuilds the whole layout (lists, dictionaries) on every Measure **and**
every Render, including the 60 fps meter animation. `Ft(...)` creates `FormattedText` per label per frame;
`UpdateDockCards`/`StyleMasterCard` allocate brushes per call. GC pauses stop every managed thread, including the
10 ms audio threads.

Fix: cache the layout until model/label-square changes; cache `FormattedText` per (text, face, size, theme version);
hoist the brushes. Pause the 10 Hz metrics timer while the window is hidden.

### Smaller defects (fixed in passing)

- `ProbeEnginePeriods` runs on every start (two activations); make it once per endpoint ID per session.
- `CalculateSyncTargetFrames`, coordinator `outputBufferMs` parameters, `SetOutputBufferMs` on the provider and
  `UiSnapshot.Rev` are dead; remove.
- Master gain while locked spams "Matrix is locked" (`MainWindow.axaml.cs:749-769` → `SetRoutes`).
- IN/LATENCY drums and master gain are not gated by lock; input-mode button is (`:641-651`), though it is a view
  filter. Lock semantics are defined in Phase 4.2.
- `MeterBars`/`MatrixControl` animation flags can stick when a frame never arrives for a hidden window.
- JITTER tile shows the structural fill sawtooth (one render gulp), not jitter.
- Zombie render thread after a failed Join keeps reporting into an old coordinator (harmless but noisy); the engine
  thread plus bounded joins make it rare, and a `_detached` flag on the provider silences it.

---

## 4. Work plan

Phases are ordered by impact ÷ risk and each is shippable on its own. Line references point at the current tree.

### Phase 0 — Diagnostics first (½ day)

0.1 `WasapiDiagnostics` (`WasapiComInterop.cs:230-285`): rotate the log at 1 MB; add `Log` calls for: start
    reason, budget breakdown (knob, cadence, gulp, ring target, render padding, effective total), per-endpoint
    actual vs enumerated format, barrier release delay, fault (already), quarantine, hard drain, hard re-anchor,
    master promotion, idle re-anchor, restart coalesced/skipped.
0.2 `AudioMatrixRouter.Tests` (xunit, net10.0): pure tests for `InputAsrc` frame conservation at ratios 1.0,
    44.1/48, 96/48; `RingBuffer` overflow + re-anchor semantics; coordinator master promotion; budget math.
    Nothing audio-device dependent.

Acceptance: `dotnet test` green; the log explains every restart.

### Phase 1 — Audio correctness (ship as v0.4.6)

1.1 **RC-1** `InputAsrc.cs:221-224`: remove the overshoot wrap; fix the `_srcFrac` comment; test from 0.2.
1.2 **RC-2** `RingBuffer`: add `LastWriteTicks`, `IsProducerLive(cadenceMs)`, `ReanchorAllConsumers(targetFrames)`
    and `AnchorGeneration`. `InputAsrc.ProcessAndWrite`: when the gap since the previous write exceeds 3× cadence
    (or this is the first write after start), call `ReanchorAllConsumers(_targetFillFrames)`.
    `MixingSampleProvider`: per-source `_anchorGen`; on change clear `_pendingSkipPerSource[i]`.
    Remove `_producedThisBlock/_producerPaceEma/_belowPaceBlocks` in favour of ring liveness (also fixes RC-11).
1.3 **RC-6** `MixingSampleProvider.cs:837-841, 928-936`: forced advance → `_timelineExtra += forced`; reported in
    `ReportConsumedAndUpdate`; no negative pending.
1.4 **RC-3a** `WasapiRenderClient.cs:166-185`: track `lastPadding`/`paddingStuckSince`; fault with a new
    `AUDCLNT_E_STALLED` sentinel (`0x8889FFFF`, internal) when padding > 0 is unchanged for ≥ 1 s.
1.5 **RC-3b** `OutputSyncCoordinator`: `PromoteMasterNoLock()` when master age > 300 ms and a follower reported
    within 100 ms; `MasterConsumerId` becomes a volatile property; `MasterChanged` event.
1.6 **RC-3c** `InputAsrc`: take `Func<string> fillConsumer` (coordinator master) instead of a string;
    `RingBuffer.SetPreferredConsumer` follows via `MasterChanged` in the engine.
1.7 **RC-4** `AudioEngine.TryInitRender/AdoptRender`: compare `ActualSampleRate/ActualChannels` with `dev.Info`;
    on mismatch replace `dev.Info` (record `with`), rebuild the provider, and if the device is the clock master
    return "rate changed" so `StartCore` rebuilds once around the new rate. `DeviceEnumerator`: forward
    `OnPropertyValueChanged` for the device-format key as a change event.
1.8 **RC-12** coordinator: projection capped at one master period; gross-error persistence counter (≥ 3 blocks);
    threshold 30 ms.
1.9 **RC-13** dispose the client on init failure; `Init`/`Start` wait with a 5 s timeout → throw → device skipped.
1.10 `ProbeEnginePeriods` once per ID (static `HashSet<string>`).

Acceptance (live, this machine, knob 20): 30 min with a continuous Steam stream: hard drains 0, SYNC 0, DROPS 0,
UNDERRUNS < 5; start the engine with Steam idle, then start streaming: audio within one fill target, no crackle
window; put the master TV to sleep: other outputs keep playing uninterrupted, log shows a promotion, not a drain
loop.

### Phase 2 — One LATENCY knob with AUTO, a budget the knob really controls (ship as v0.5.0)

Revised after v0.4.6: the IN knob is dropped (its only honest job, headroom for a bursty input, is done better
from measurements), and the engine owns headroom through a self-tuning loop that reacts to underruns.

2.1 `WasapiRenderClient`: `TargetPaddingFrames` (settable live); pre-roll **target** frames of silence; per event
    write `clamp(target − padding, 0, bufferFrames − padding)`; stall = no progress for 1 s. `ActualBufferMs`
    stays the allocation; `FifoMs` = target padding is what latency reports. Thread exit without a Stop request is
    a fault (both clients).
2.2 Pure `LatencyBudget` (tested) replaces the ad-hoc math:
    - `period` = worst render engine period, `cadence` = worst capture cadence, `buffer` = smallest real render buffer
    - `renderBufferRequestMs = clamp(knob / 2, 2·period, 100)` (allocation; AUTO → 2 periods)
    - `renderPaddingMs = clamp(knob / 4, period + 2 + safetyPeriods·period, buffer)` — uniform across outputs so
      FIFO depths stay equal (the sync coordinator assumes that)
    - `renderLatencyMs = renderPaddingMs + period`
    - `ringFloorMs = renderPaddingMs + cadence + autoHeadroomMs`
    - `ringTargetMs = AUTO ? ringFloorMs : max(ringFloorMs, knob − cadence − renderLatencyMs)`
    - `effectiveTotalMs = cadence + ringTargetMs + renderLatencyMs`
    `MetricsSnapshot` carries `EffectiveLatencyMs`, `LatencyFloorMs`, `AutoHeadroomMs`, `SafetyTier`.
2.3 Live retarget on knob change (no restart): ASRC targets, coordinator release target and render padding update
    in place; a ring-target decrease > 5 ms applies one `TrimBacklogBy` on every ring (one small skip, outputs stay
    aligned); an increase applies one equal `HoldFor` on every provider (one small gap). Restart only when the
    needed padding exceeds the allocated buffer.
2.4 **AUTO headroom loop** (engine health tick, 1 s): per output, underruns over the last 10 s against live
    producers. ≥ 3 → `autoHeadroomMs += cadence` (cap 40, 10 s cooldown), logged. Still ≥ 3 at the cap for 30 s →
    safety tier (render padding +1 period). 5 clean minutes → step back down one notch at a time. Latency creeps
    only as far as the machine needs and comes back when load drops.
2.5 Config: `ConfigVersion` 2; `OutputBufferMs` 0 = AUTO (default); migration resets a saved knob < 50 to AUTO and
    `InputBufferMs` to 10 (unused). `KeepAlive` (default on) and `KeepSystemAwakeWhileRunning` (default off).
2.6 UI: the IN drum is removed; the LATENCY drum spans its slot, minimum position reads **AUTO**, and its caption
    always shows the effective value (`LATENCY · 54`), so "nothing happens" cannot recur. Metrics timer pauses
    while hidden; meter animations cannot wedge after a hidden spell.

Acceptance: AUTO total ≈ 55 ms on this setup (was ~80–90); knob sweep above the floor moves the total tile
monotonically with no restart; a CPU-load test (compile in the background) raises headroom automatically and the
drum caption shows it; UNDERRUNS stop climbing once headroom settles.

### Phase 3 — Threading, hotplug, recovery

3.1 **Engine thread.** New `EngineWorker` in Core: one dedicated MTA thread, `BlockingCollection<Action>`,
    `Post`, `Invoke<T>(timeout)`. `AudioEngine` is only ever touched from that thread. `AppController` public
    mutators become posts; `StateChanged` and a new `MetricsChanged` are produced on the engine thread (its own
    100 ms timer, enabled only while the window is visible) and marshalled to the UI. `GetSnapshot()` returns the
    last pushed snapshot; `GetMetrics()` is removed; `SetRoutes` returns void and raises `RouteErrors`.
    `MainWindow()` no longer blocks: it posts `Initialize` and renders the first snapshot when it arrives.
    Touches: `AppController.cs` (whole file), `AudioEngine.cs` (public surface unchanged, add thread assertions),
    `MainWindow.axaml.cs:77-156, 587-622, 1247-1284`.
3.2 Adaptive degrade (`AudioEngine.cs:166-242`) becomes a posted action on the engine thread.
3.3 `DeviceEnumerator`: cache `DeviceInfo` by ID; `MixFormat` queried once per ID; invalidated by state/property
    notifications; `GetDevices` returns from cache. `AddXxxDevice` take the cached list.
3.4 **RC-5** restart coalescing + per-device quarantine (`ActiveDevice.FaultCount/QuarantinedUntil`);
    `RequestRestart(reason)` single entry point with a 1 s cooldown; `ReloadEngine` is the user's explicit
    "force" and bypasses quarantine.
3.5 **RC-10** `EnsureRunning()` with backoff, replacing `_startupRetryTimer`; honours `Enabled`.
3.6 **RC-9** identity remap by (flow, name, channels) in `RefreshDevices` and `AppConfig.ApplyToEngine`; prune
    stale duplicates; apply to UI prefs via a `DeviceIdRemapped(old, new)` event the window handles.
3.7 **Incremental join/leave** (removes the last reason for global restarts):
    - Output join while running: register its consumer on every ring at `writePos − ringTarget` (ring data in
      that window is exactly what the master plays now), `coordinator.JoinLive(id, masterTimeline)`, init +
      play with no barrier. Output leave: detach + dispose only. Routing to a parked output → join.
    - Input join: new ring + ASRC; every consumer registered at `−ringTarget` (ring is zeroed, so the first
      target window is silence, then live audio at the normal latency). Input leave: detach ring from providers
      (sources list becomes copy-on-write).
    - Master leave or stall: `PromoteMaster` live; full restart only if the new master's rate differs.
    `AddInputDevice/AddOutputDevice/RemoveXxx` (`AudioEngine.cs:741-972`) lose their Stop/Start;
    `BeginDeviceBatch` becomes unnecessary and is removed.
3.8 Shutdown: `Stop` joins with 250 ms per endpoint, threads are background; quit waits ≤ 1.5 s total.
3.9 Bounded enumeration: `RefreshDevices` logs its duration; anything > 500 ms is a log warning with the slow ID.
3.10 **OS load immunity** (ships with Phase 2): opt the process out of Windows 11 power throttling
    (`SetProcessInformation(ProcessPowerThrottling)`, execution speed + ignore-timer-resolution), because a tray
    process otherwise loses its 1 ms timer resolution and gets EcoQoS under load — the "bad while I work on the
    laptop" case. Optional `SetThreadExecutionState(SYSTEM_REQUIRED)` while routing (config, off by default).
3.11 **In-process health monitor** (ships with Phase 2, engine health tick): per-output coordinator report age
    > 2 s with routes active → fault that device through the normal backoff path; audio thread exit without a
    Stop request → fault; UI-thread heartbeat age > 15 s → stop the process heartbeat so the watchdog acts.
3.12 **Keep-alive watchdog** (ships with Phase 2): the same exe in `--watchdog <pid>` mode, spawned by the app
    (mutex-guarded, `--no-watchdog` disables). The engine health tick writes a timestamp to a memory-mapped
    heartbeat every second. Heartbeat older than 10 s, or a non-zero exit, → kill and relaunch with `--startup`;
    clean exit → the watchdog exits; crash-loop guard: three relaunches within five minutes pause it for five
    minutes. Unhandled exceptions are logged with their stack before the process dies.

Acceptance: TV sleep/wake drills (×10) with a continuous stream: no silence on the other outputs, routes restored
on the new ID without touching the UI, no UI stall > 50 ms (measured with a dispatcher watchdog in debug builds),
config stops accumulating duplicates.

### Phase 4 — UI/UX

4.1 Power: `AppController.Enabled` persisted in config (not prefs), applied at launch, honoured by every auto-start.
4.2 Lock semantics (one table, enforced in the controller, reflected in the UI):

    | Blocked by lock | Free while locked |
    |---|---|
    | route on/off, gain, phase, clear | mute, power, reload |
    | master selection (input/output) | view toggle, show-all, input-mode filter |
    | IN / LATENCY knobs, master gain | theme pickers, window, update |

    Drums render disabled (muted value, no texture roll) when locked; no banner spam.
4.3 Device status in `DeviceSnapshot` (`Running / Parked / Opening / Failed / Quarantined / Stalled`) → header
    card: thin status strip and tooltip; dock card: same; a fault shows a 4 s banner with the device name.
4.4 Metrics tiles: 10 s rate as the value, lifetime total + "since engine start Xs" in the tooltip; click resets;
    JITTER = p2p fill swing minus one render gulp (label stays).
4.5 Hidden window: stop the metrics timer on hide, restart on show; reset `_animating` flags on show.
4.6 **RC-14** render perf: cached layout; `FormattedText` cache keyed by (text, face, size, `Theme.Version`);
    hoisted brushes in `UpdateDockCards/StyleMasterCard/ShowPicker`.
4.7 Remove dead `Rev` plumbing and dead coordinator/provider parameters.
4.8 Tooltips: IN/LATENCY per 2.4; LATENCY tile tooltip lists the budget terms.

### Phase 5 — Verification protocol (run after each phase, record in the log)

1. Soak: 30 min continuous Steam stream, knob at the floor. Pass: hard drains 0, SYNC 0, DROPS 0, UNDERRUNS < 5,
   no fault, all three outputs audibly aligned (clap test across TV + HDMI).
2. Silent start: engine starts with Steam idle; start streaming after 60 s. Pass: audio appears within one ring
   target, no crackle window, UNDERRUNS unchanged.
3. Master sleep: put the master TV to sleep mid-stream. Pass: other outputs uninterrupted, log shows promotion,
   TV rejoins on wake with its routes, no restart of the others (Phase 3.7) or exactly one restart (before 3.7).
4. ID churn: unplug/replug an HDMI cable; confirm the new endpoint ID is remapped and the config entry count does
   not grow.
5. Format change: switch an output to 44.1 kHz in Sound settings while running. Pass: pitch unchanged, only that
   device reopens.
6. Knob sweep: 10 → 200 → 10 without restarts; total tile tracks the knob above the floor; floor hint visible.
7. Tray: hide, wait 10 min, show. Pass: meters animate, no stale metrics, UI responsive.
8. Boot: reboot with start-at-boot; devices appear late. Pass: engine starts within the backoff, no manual reload.
9. UI stall watchdog (debug): no dispatcher stall > 50 ms during any of the above.

---

## 5. Order of shipping and risk

| Release | Phases | Risk | Why this order |
|---|---|---|---|
| v0.4.6 | 0, 1 | low: local changes, tests cover the math | fixes the chronic drain, the dead-input start, the stalled-master poisoning and the pitch shift: the loudest symptoms |
| v0.5.0 | 2, 3 | medium: engine thread and incremental join are structural | the knobs become real and the restart storms end |
| v0.5.1 | 4 | low | polish and visibility |

Not in scope: exclusive-mode WASAPI, sub-10 ms periods on devices that do not advertise them, acoustic alignment
across devices (per-device delay stays manual).

---

## Appendix A — Latency floor on this setup after Phase 2

| Term | Today | After | Note |
|---|---|---|---|
| Capture cadence | 10 | 10 | fixed by the VB driver |
| Ring fill target | 36 | 22 | 12 ms gulp + IN 10 |
| Render FIFO | 21 (+10 period) | 12 (+10 period) | padding target instead of full buffer |
| **Total** | **≈ 77** | **≈ 44–46** | knob effective from here upward |

## Appendix B — Config migration

`ConfigVersion` (new, default 0 for existing files). On load with version < 2: `InputBufferMs` 40 → 10;
duplicate device entries collapsed by (flow, name, channels) keeping the entry whose ID is currently present;
`Window`, routes and prefs untouched. Saved as version 2.

## Appendix C — Thread ownership after Phase 3

| Thread | Owns | Never does |
|---|---|---|
| UI (Avalonia) | snapshots, prefs, drawing | touch `AudioEngine` |
| Engine worker | `AudioEngine` state, config save, device enumeration, restarts, metrics build | block > 5 s on any driver call |
| Per-endpoint audio threads | COM objects, `Read`/capture callbacks, ring writes | restart anything; they only raise `Faulted` |
